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
    /// Throttles disk and cloud saves. It is independent of the models and delays Action delegates.
    ///
    /// Multi-owner: each logical owner (EconomyService, ProgressionService, ...) has its own pending
    /// slot. In the single-slot design the second owner's TryRequestSave overwrote the first owner's
    /// pending save, and unless the window flushed first, the first owner's write was SILENTLY LOST:
    /// real data loss when two throttled services shared the singleton. The failure backoff and the
    /// retry cap (M1) are per owner too: the cap of an owner that keeps failing cannot clear another
    /// owner's pending flag.
    /// </summary>
    public class SaveThrottler : ISaveThrottler, INexusService, IUnpausableTickable
    {
        [Inject] public ITickService TickService { get; set; }
        [Inject] public ITimeProvider TimeProvider { get; set; }
        /// <summary>The context's bus, for <see cref="ApplicationPauseSignal"/>; optional so a throttler built by hand still works.</summary>
        [OptionalInject] public ISignalBus SignalBus { get; set; }

        private ISignalSubscription _pauseSubscription;
        private bool _focusHooked;
        // Guarded by _lock. Set before OnDispose's flush: a save in flight on another thread then writes the newer request
        // that arrived during it, since no Tick or Flush will (issue #323).
        private bool _disposing;

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
            public long RequestVersion;
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
            // Unpausable: a save already pending when a screen or an ad holds the pause is written when its window ends,
            // not when the pause ends, so a crash during the pause does not lose it (issue #202)
            TickService?.RegisterTickable(this);
            // A pending save waited for Tick, which a paused TickService skips (an ad holds the pause) and a process killed in
            // the background never reaches: the economy or progression change was lost with no line (issue #167). The app
            // losing focus or pausing now writes it, synchronously, because Android may freeze the process right after.
            _pauseSubscription = SignalBus?.Subscribe<ApplicationPauseSignal>(OnApplicationPause);
            Application.focusChanged += OnFocusChanged;
            _focusHooked = true;
            return default;
        }

        private void OnApplicationPause(ApplicationPauseSignal signal)
        {
            if (signal.IsPaused) FlushPending();
        }

        private void OnFocusChanged(bool hasFocus)
        {
            if (!hasFocus) FlushPending();
        }

        public void OnDispose()
        {
            TickService?.UnregisterTickable(this);
            _pauseSubscription?.Dispose();
            _pauseSubscription = null;
            if (_focusHooked)
            {
                Application.focusChanged -= OnFocusChanged;
                _focusHooked = false;
            }
            // Persist every owner's pending (or last) save so no throttled write is lost
            // at teardown, then drop the slots.
            lock (_lock) { _disposing = true; }
            Flush();
            lock (_lock) { _slots.Clear(); }
        }

        /// <summary>
        /// Writes every owner's pending save now, whatever its throttle window, for the app going to the background. An owner
        /// with nothing pending is not written again, so focus loss followed by pause (Android) writes once.
        /// </summary>
        private void FlushPending()
        {
            List<SaveSlot> due = null;
            lock (_lock)
            {
                foreach (var kvp in _slots)
                {
                    if (!kvp.Value.Pending) continue;
                    kvp.Value.Pending = false; // claim before releasing the lock, as Tick does
                    (due ??= new List<SaveSlot>(2)).Add(kvp.Value);
                }
            }
            if (due == null) return;
            for (int i = 0; i < due.Count; i++) FlushSlot(due[i]);
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
                slot.RequestVersion++;
                // Fresh slot (LastSaveTime < 0) or window elapsed → flush immediately.
                flushNow = slot.LastSaveTime < 0f || Now - slot.LastSaveTime >= _throttleSeconds;
                slot.Pending = !flushNow;
            }

            // Invoke OUTSIDE the lock: the save action can re-enter TryRequestSave and
            // must not deadlock or mutate the slot dictionary mid-iteration.
            if (flushNow) FlushSlot(slot);
        }

        // Reusable per-thread due-slot buffer so Tick stays at 0 B in steady state (harness GCA8)
        // (NEXUS001 flags per-frame collection allocation inside hot-path methods).
        // Re-entrancy-safe: a save action that (pathologically) re-enters Tick on the same
        // thread rents a FRESH list via the depth guard (mirrors ObjectPoolService's
        // _poolableCallbackDepth convention) instead of clobbering the outer drain.
        [ThreadStatic] private static List<SaveSlot> s_tickDueBuffer;
        [ThreadStatic] private static int s_tickDepth;

        private static List<SaveSlot> GetTickDueBuffer()
        {
            if (s_tickDepth > 0) return new List<SaveSlot>(2); // nested Tick: fresh list
            var buffer = s_tickDueBuffer;
            if (buffer == null) buffer = s_tickDueBuffer = new List<SaveSlot>(2);
            return buffer;
        }

        public void Tick(float deltaTime)
        {
            // Claim the due slots under the lock, then flush OUTSIDE it. Save actions are
            // arbitrary user code (disk I/O, cloud calls) that may re-enter
            // TryRequestSave/ForceSave or block waiting on another thread that itself
            // wants _lock; holding the lock across action.Invoke() would serialize every
            // save behind the slowest action and risk a deadlock (see TryRequestSave's
            // "Invoke OUTSIDE the lock" invariant). The per-slot Flushing flag already
            // prevents two callers from ever running the same action concurrently.
            var due = GetTickDueBuffer();
            s_tickDepth++;
            try
            {
                lock (_lock)
                {
                    due.Clear();
                    foreach (var kvp in _slots)
                    {
                        var slot = kvp.Value;
                        if (slot.Pending && Now - slot.LastSaveTime >= _throttleSeconds)
                        {
                            slot.Pending = false; // claim before releasing the lock
                            due.Add(slot);
                        }
                    }
                }
                for (int i = 0; i < due.Count; i++) FlushSlot(due[i]);
            }
            finally
            {
                s_tickDepth--;
            }
        }

        public void ForceSave(Action saveAction) => ForceSave(DefaultOwner, saveAction);

        public void ForceSave(string owner, Action saveAction)
        {
            if (saveAction == null) return;

            SaveSlot slot;
            lock (_lock)
            {
                slot = GetSlotLocked(owner);
                slot.LastAction = saveAction;
                slot.RequestVersion++;
                slot.Pending = false;
            }
            FlushSlot(slot);
        }

        /// <summary>Flushes EVERY owner's last save, pending or not — the "persist everything now" path (dispose,
        /// explicit flush). Focus loss and pause write only the pending saves. One owner's failure never stops the others.</summary>
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

        private void FlushSlot(SaveSlot slot, bool disposeRetry = false)
        {
            Action action;
            long requestVersion;
            lock (_lock)
            {
                // The action is RETAINED, not consumed: Flush()/OnDispose must be able to
                // re-persist an owner's current state even when nothing new was requested
                // (a save is idempotent — writing the same state twice is harmless, losing
                // it is not). What must never happen is two callers running the same action
                // at the same time, so execution is guarded by a per-slot in-flight flag.
                if (slot.Flushing)
                {
                    // Another caller is mid-save for this slot. A concurrent request
                    // (TryRequestSave/ForceSave) may already have overwritten LastAction
                    // with a NEWER state than the in-flight save captured. Queue it as
                    // pending so the next Tick/Flush persists it — otherwise the newest
                    // state is silently dropped until the next explicit request.
                    slot.Pending = true;
                    return;
                }
                action = slot.LastAction;
                if (action == null) return;
                requestVersion = slot.RequestVersion;
                slot.Pending = false;
                slot.Flushing = true;
            }

            bool retry;
            try
            {
                action.Invoke();
                lock (_lock)
                {
                    slot.LastSaveTime = Now;
                    slot.Pending = slot.RequestVersion != requestVersion;
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
                    bool superseded = slot.RequestVersion != requestVersion;
                    slot.ConsecutiveFailures = superseded ? 0 : slot.ConsecutiveFailures + 1;
                    slot.LastSaveTime = Now;
                    slot.Pending = superseded || slot.ConsecutiveFailures < MaxConsecutiveSaveFailures;
                }
                NexusLog.WarnAlways(
                    $"[SaveThrottler] Save execution failed ({slot.ConsecutiveFailures}/{MaxConsecutiveSaveFailures} consecutive): {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    slot.Flushing = false;
                    // A newer request arrived during this save and dispose skipped the busy slot: write it once more here,
                    // one attempt only, so an action that keeps failing or re-requesting cannot loop (issue #323).
                    retry = !disposeRetry && _disposing && slot.RequestVersion != requestVersion;
                }
            }
            if (retry) FlushSlot(slot, disposeRetry: true);
        }
    }
}
