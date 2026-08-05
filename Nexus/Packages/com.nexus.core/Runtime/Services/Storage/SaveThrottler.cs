using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Core.Services
{
    public interface ITimeProvider
    {
        float Now { get; }
    }

    public class UnityTimeProvider : ITimeProvider
    {
        // Time.realtimeSinceStartup is main-thread-only, but SaveThrottler is documented
        // callable from non-main threads. Use a monotonic Stopwatch instead — the
        // throttler only compares differences, so the absolute time base is irrelevant.
        private static readonly System.Diagnostics.Stopwatch s_clock = System.Diagnostics.Stopwatch.StartNew();

        public float Now => (float)s_clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Disk ve bulut kayıt işlemlerini throttle (kısıtlayarak) gerçekleştiren servis.
    /// Modellerden tamamen bağımsız çalışır ve Action delegate'lerini geciktirir.
    ///
    /// Multi-owner: her mantıksal sahip (EconomyService, ProgressionService, ...) kendi
    /// pending slot'una sahiptir. Tek-slot tasarımında ikinci sahibin TryRequestSave çağrısı
    /// birincinin bekleyen kaydını üzerine yazar ve pencere dolmadan flush olmazsa ilk
    /// sahibin yazımı SESSİZCE KAYBOLUYORDU — iki throttled servis aynı singleton'ı
    /// paylaştığında gerçek veri kaybı. Hata yedeği + yeniden deneme tavanı (M1) de
    /// owner başına izole edilmiştir: sürekli başarısız olan bir sahibin tavanı, diğer
    /// sahibin bekleyen bayrağını temizleyemez.
    /// </summary>
    public class SaveThrottler : ISaveThrottler, INexusService, ITickable
    {
        [Inject] public ITickService TickService { get; set; }
        [Inject] public ITimeProvider TimeProvider { get; set; }

        private readonly float _throttleSeconds = 2f;

        private sealed class SaveSlot
        {
            public Action LastAction;
            public float LastSaveTime = -999f;
            public bool Pending;
            public int ConsecutiveFailures;
            /// <summary>Set while this slot's action is executing, so two callers (Tick,
            /// ForceSave, Flush) can never run the same save concurrently.</summary>
            public bool Flushing;
        }

        // Owner id → slot. Guarded by _lock: services may request from different threads
        // (economy mutations from gameplay/network threads) while Tick() drains on the
        // TickService driver thread.
        private readonly Dictionary<string, SaveSlot> _slots = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        private const string DefaultOwner = "default";

        // Consecutive-failure accounting. A failing save must back off (the throttle
        // window gates the next retry) instead of retrying on every request in a tight
        // loop, and must give up after a bound so a permanently broken disk cannot keep
        // the pending flag alive forever. Per-owner.
        private const int MaxConsecutiveSaveFailures = 5;

        public SaveThrottler()
        {
            _throttleSeconds = 2f;
        }

        public SaveThrottler(ITickService tickService, TimeSpan throttleTime) : this()
        {
            if (tickService != null) TickService = tickService;
            _throttleSeconds = (float)throttleTime.TotalSeconds;
        }

        // Thread-safe monotonic fallback clock (Time.realtimeSinceStartup would throw off
        // the main thread; see UnityTimeProvider).
        private static readonly System.Diagnostics.Stopwatch s_fallbackClock = System.Diagnostics.Stopwatch.StartNew();

        private float Now => TimeProvider?.Now ?? (float)s_fallbackClock.Elapsed.TotalSeconds;

        public float SecondsSinceLastSave => GetSecondsSinceLastSave(DefaultOwner);

        public float GetSecondsSinceLastSave(string owner)
        {
            // Read-only: never creates a slot (probing an unused owner must not mutate
            // state). An untouched owner has no save history → 999 (matches the fresh-slot
            // sentinel the write paths use).
            lock (_lock)
            {
                if (owner != null && _slots.TryGetValue(owner, out var slot))
                    return slot.LastSaveTime < 0f ? 999f : Now - slot.LastSaveTime;
                return 999f;
            }
        }

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            TickService?.RegisterTickable(this);
            return default;
        }

        public void OnDispose()
        {
            TickService?.UnregisterTickable(this);
            // Persist every owner's pending (or last) save so no throttled write is lost
            // at teardown, then drop the slots.
            Flush();
            lock (_lock) { _slots.Clear(); }
        }

        public void TryRequestSave(Action saveAction) => TryRequestSave(DefaultOwner, saveAction);

        public void TryRequestSave(string owner, Action saveAction)
        {
            if (saveAction == null) return;

            SaveSlot slot;
            bool flushNow;
            lock (_lock)
            {
                slot = GetSlotLocked(owner);
                slot.LastAction = saveAction;
                // Fresh slot (LastSaveTime < 0) or window elapsed → flush immediately.
                flushNow = slot.LastSaveTime < 0f || Now - slot.LastSaveTime >= _throttleSeconds;
                slot.Pending = !flushNow;
            }

            // Invoke OUTSIDE the lock: the save action can re-enter TryRequestSave and
            // must not deadlock or mutate the slot dictionary mid-iteration.
            if (flushNow) FlushSlot(slot);
        }

        public void Tick(float deltaTime)
        {
            SaveSlot[] ready;
            lock (_lock)
            {
                List<SaveSlot> due = null;
                foreach (var kvp in _slots)
                {
                    var slot = kvp.Value;
                    if (slot.Pending && Now - slot.LastSaveTime >= _throttleSeconds)
                    {
                        slot.Pending = false; // claim before releasing the lock
                        (due ??= new List<SaveSlot>(2)).Add(slot);
                    }
                }
                ready = due?.ToArray() ?? s_emptySlots;
            }
            for (int i = 0; i < ready.Length; i++) FlushSlot(ready[i]);
        }

        private static readonly SaveSlot[] s_emptySlots = new SaveSlot[0];

        public void ForceSave(Action saveAction) => ForceSave(DefaultOwner, saveAction);

        public void ForceSave(string owner, Action saveAction)
        {
            if (saveAction == null) return;

            SaveSlot slot;
            lock (_lock)
            {
                slot = GetSlotLocked(owner);
                slot.LastAction = saveAction;
                slot.Pending = false;
            }
            FlushSlot(slot);
        }

        /// <summary>Flushes EVERY owner's last save — the "persist everything now" path
        /// (dispose, focus loss, explicit flush). One owner's failure never stops the others.</summary>
        public void Flush()
        {
            SaveSlot[] all;
            lock (_lock)
            {
                if (_slots.Count == 0) return;
                all = new SaveSlot[_slots.Count];
                _slots.Values.CopyTo(all, 0);
            }
            for (int i = 0; i < all.Length; i++) FlushSlot(all[i]);
        }

        public void Flush(string owner)
        {
            SaveSlot slot;
            lock (_lock) { slot = GetSlotLocked(owner); }
            FlushSlot(slot);
        }

        private SaveSlot GetSlotLocked(string owner)
        {
            if (owner == null) owner = DefaultOwner;
            if (!_slots.TryGetValue(owner, out var slot))
            {
                slot = new SaveSlot();
                _slots[owner] = slot;
            }
            return slot;
        }

        private void FlushSlot(SaveSlot slot)
        {
            Action action;
            lock (_lock)
            {
                // The action is RETAINED, not consumed: Flush()/OnDispose must be able to
                // re-persist an owner's current state even when nothing new was requested
                // (a save is idempotent — writing the same state twice is harmless, losing
                // it is not). What must never happen is two callers running the same action
                // at the same time, so execution is guarded by a per-slot in-flight flag.
                if (slot.Flushing) return;
                action = slot.LastAction;
                if (action == null) return;
                slot.Flushing = true;
            }

            try
            {
                action.Invoke();
                lock (_lock)
                {
                    slot.LastSaveTime = Now;
                    slot.Pending = false;
                    slot.ConsecutiveFailures = 0;
                }
            }
            catch (Exception ex)
            {
                // Back off after a failed save. Treating the failed attempt as a save
                // moment makes the throttle window gate the next retry (no tight loop), and
                // the retry cap clears the pending flag after repeated failures so a broken
                // disk cannot keep retrying forever. Per-owner, so one owner's failure never
                // clears another owner's pending save. The failure is always logged — never
                // silent.
                lock (_lock)
                {
                    slot.ConsecutiveFailures++;
                    slot.LastSaveTime = Now;
                    slot.Pending = slot.ConsecutiveFailures < MaxConsecutiveSaveFailures;
                }
                NexusRuntime.Logger?.LogWarning(
                    $"[SaveThrottler] Save execution failed ({slot.ConsecutiveFailures}/{MaxConsecutiveSaveFailures} consecutive): {ex.Message}");
            }
            finally
            {
                lock (_lock) { slot.Flushing = false; }
            }
        }
    }
}
