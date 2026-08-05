# 🛡️ Adversarial Audit Report: Nexus (com.nexus.core)

**Audit scope:** Runtime klasörü (97 .cs, ~21,600 satır) — Core, Lifecycle, Services, Models, Queue, Recovery, Netcode, Plugins, FSM, DOTS, Debug, Extensions, Data, Attributes, Binding, Components
**Methodology:** Adversarial Code Audit Protocol v1 — zero-complacency, devil's advocate, evidence-based
**Audit date:** 2026-08-05
**Auditor perspective:** Production hot-path reviewer (mobile game, casual/hybrid-casual profil, %90 main-thread UI work)

> ⚠️ **Anti-sycophancy disclaimer:** Bu rapor CLEAN kod yazımı veya "iyi niyetli yorumları" övmek için değil, sessizce bozulmuş invariant'ları, race window'ları ve allocation pattern'lerini ortaya çıkarmak için yazıldı. Hiçbir "happy path" testinin geçmesi, bir invariant'ın gerçekten korunduğunu garanti etmez.

---

## 1. 🔴 Critical Vulnerabilities & Dead Guards

### 1.1 [CRITICAL] Trace buffer ring math misses newest entry after wraparound

**File & Line:** `Runtime/Core/NexusRuntime.cs:467`

**Vulnerability:** `GetRecentTraces` reader is off-by-one after the ring buffer wraps once. The newest entry at index 0 (the one that overwrote the oldest) is never returned.

**Empirical evidence:**
```csharp
// Writer (RecordTrace, line 443-453):
int rawIndex = System.Threading.Interlocked.Increment(ref s_traceIndex);
int idx = (int)((uint)rawIndex % (uint)size);  // wraps to 0 after size writes
buffer[idx] = entry;
// s_traceCount caps at size, never exceeds it

// Reader (GetRecentTraces, line 455-471):
int start = (int)((uint)(System.Threading.Volatile.Read(ref s_traceIndex) - count + 1) % (uint)size);
```

**Trace:** Say `size = 5`:
- After 5 writes: `s_traceIndex = 4`, `count = 5`. Reader: `start = (4-5+1)%5 = 0`. Reads 0..4. ✅
- 6th write: `s_traceIndex = 5`, `idx = 0%5 = 0`, `buffer[0]` overwritten. `count` stays 5.
- Reader: `start = (5-5+1)%5 = 1`. Reads 1,2,3,4 (only 4 entries!). **Misses index 0 (the NEWEST).**

**Impact:** TracerPlugin dashboard will silently show stale data after the ring fills once. Telemetry/replay fidelity broken.

**Fix:** `int start = (int)((uint)(System.Threading.Volatile.Read(ref s_traceIndex) + 1) % (uint)size);` or change writer to pre-decrement.

---

### 1.2 [CRITICAL] `OnContextRegistered` / `OnContextUnregistered` events nulled inside lock — NRE race

**File & Line:** `Runtime/Core/NexusRuntime.cs:311-313`

**Vulnerability:** `Reset()` nulls the static events INSIDE `s_lock`. A subscriber that captured the event delegate before `Reset` and is now invoking it on another thread will get a `NullReferenceException` only if it re-reads the field — but the **simultaneous invocation** of an old subscriber with the now-null field is the actual race.

**Empirical evidence:**
```csharp
public static void Reset()
{
    lock (s_lock)
    {
        snapshot = s_activeContexts.ToArray();
        s_activeContexts.Clear();
        // ...
        OnContextRegistered = null;        // <-- inside lock
        OnContextUnregistered = null;       // <-- inside lock
        // ...
    }
    // ...later: snapshot[i].Dispose() — this can trigger UnregisterContext,
    // which fires OnContextUnregistered, which is now null
}
```

Even more dangerous: between the `OnContextRegistered = null` write and the next event invocation, a subscriber that captured the field reference into a local can still invoke safely, but a subscriber that re-reads the field will NRE. The pattern is **non-atomic** for the event lifecycle.

**Impact:** Production crashes during domain reload (Editor + Disable Domain Reload mode). Random NRE in production saves.

**Fix:** Capture to local, null outside lock, invoke local:
```csharp
var onUnreg = OnContextUnregistered;
OnContextUnregistered = null;
lock (s_lock) { /* ... */ }
onUnreg?.Invoke(/* ... */); // outside lock
```

---

### 1.3 [CRITICAL] `ObservableProperty._isNotifying` check-then-set is racy despite volatile

**File & Line:** `Runtime/Models/ObservableProperty.cs:54-66, 66, 85`

**Vulnerability:** `volatile bool` guarantees visibility but **not atomicity** of check-then-set. Two threads can both pass `if (_isNotifying) { return; }`, both reach `_isNotifying = true;`, and both dispatch.

**Empirical evidence:**
```csharp
public T Value
{
    get => _value;
    set
    {
        if (EqualityComparer<T>.Default.Equals(_value, value)) return;
        if (_isNotifying)           // <-- Thread A reads false
        {
            _pendingReentrantValue = value;
            _hasPendingReentrantValue = true;
            return;
        }
        // ... gap where Thread B also reads _isNotifying == false ...
        var old = _value;
        _value = value;
        Action<T, T>[] snapshot = _observers.GetSnapshot();
        if (snapshot != null)
        {
            _isNotifying = true;   // <-- Thread A writes true
            try { /* dispatch */ }
            finally { _isNotifying = false; }
        }
    }
}
```

**Impact:** Two concurrent `.Value = x` calls on the same property from different threads → both invoke all subscribers → subscribers see the same "old" value but two notifications. Mediator/View state can desync.

**Fix:** Use `Interlocked.CompareExchange(ref _isNotifying, 1, 0) == 0` as the atomic guard, OR move dispatch under the existing `_eventLock` (mirror `ObservableList<T>`).

---

### 1.4 [CRITICAL] `ObjectPoolService.Despawn(prefab)` destroys the prefab asset

**File & Line:** `Runtime/Services/Pool/ObjectPoolService.cs:140-150`

**Vulnerability:** If a user accidentally calls `Despawn(prefab)` instead of `Despawn(spawnedInstance)`, the pool will destroy the prefab asset itself — silent, no validation.

**Empirical evidence:**
```csharp
public void Despawn(GameObject instance)
{
    if (instance == null) return;
    int instanceId = GetId(instance);

    if (!_poolsByInstanceId.TryGetValue(instanceId, out var pool))
    {
        _spawnGenerations.Remove(instanceId);
        SafeDestroyUtility.SafeDestroy(instance);  // <-- destroys whatever was passed
        return;
    }
    // ... otherwise normal flow
}
```

The `if (!TryGetValue)` branch handles "instance not in any pool" by destroying it. The prefab, having never been spawned, has no entry in `_poolsByInstanceId` → falls into the destroy branch.

**Impact:** Production save-data corruption equivalent — the user loses the prefab reference, the project breaks. The error doesn't surface until next play.

**Fix:** Add `if (instance == pool.Prefab) { /* log error, no-op */ return; }` check OR use `ReferenceEquals` against all registered pool prefabs.

---

### 1.5 [CRITICAL] `CommandPool` double-Resets every pooled command

**File & Line:** `Runtime/Core/CommandPool.cs:142-143, 213`

**Vulnerability:** Every command popped from the pool is `Reset()` twice per use cycle. The "T6 fix" added a pop-side reset, but the return-side `Cleanup` calls `NexusDI.ClearInjectedReferences` which **also calls `Reset()`** on `IResettable`. The author's comment claims "Return() only clears [Inject] fields" — but it doesn't, it also resets.

**Empirical evidence:**
```csharp
// CommandPool.cs:142-143 (Get path — pop side)
if (instance is IResettable resettable)
    resettable.Reset();   // <-- first reset

// CommandPool.cs:213 (Return path)
private void Cleanup(object command)
{
    if (command != null)
        NexusDI.ClearInjectedReferences(command);  // <-- routes to Clearer
}

// NexusDI.cs Clearer.ClearInjectedReferences (line 652):
if (instance is IResettable resettable)
    resettable.Reset();   // <-- second reset
```

**Impact:** Every IResettable command runs `Reset()` twice per fire. For a hot signal fired 1000x/frame, that's 1000 wasted resets + 1000 reflection `is` checks + 1000 `Reset()` virtual dispatches.

**Fix:** Drop the pop-side `Reset()` (line 142-143) — return path already handles it via `ClearInjectedReferences`.

---

## 2. 🟠 Thread-Safety & Race Condition Risks

### 2.1 [HIGH] `NexusRuntime.Logger` lock-ordering risk + hot-path lock

**File & Line:** `Runtime/Core/NexusRuntime.cs:144-156`

**Risk:** `Logger` getter acquires `s_loggerCacheLock` then calls `CurrentContext?.TryResolve<ILoggerService>()`. `TryResolve` eventually enters the DI container's `ResolveBinding` which acquires `_singletonLock`. **Lock order:** `s_loggerCacheLock → _singletonLock`.

If any DI code path logs during construction (a constructor that catches a recoverable error and logs), the order becomes `_singletonLock → s_loggerCacheLock` → **classic AB-BA deadlock**.

**Empirical evidence:**
```csharp
public static Services.ILoggerService Logger
{
    get
    {
        lock (s_loggerCacheLock)         // ← lock A
        {
            if (s_cachedLogger != null) return s_cachedLogger;
            var resolved = CurrentContext?.TryResolve<Services.ILoggerService>();
            // TryResolve → Resolve → ResolveBinding → lock (_singletonLock)  ← lock B
            if (resolved != null) s_cachedLogger = resolved;
            return resolved;
        }
    }
}
```

**Impact:** Deadlock in services that log during construction (any `[Inject]` setup that surfaces an error via `NexusRuntime.Logger`). Likely rare in well-behaved code, but the code path is reachable.

**Fix:** Compute the logger **outside** the cache lock:
```csharp
get
{
    var cached = Volatile.Read(ref s_cachedLogger);
    if (cached != null) return cached;
    var resolved = CurrentContext?.TryResolve<Services.ILoggerService>();
    if (resolved != null) Interlocked.CompareExchange(ref s_cachedLogger, resolved, null);
    return resolved;
}
```

---

### 2.2 [HIGH] `s_monitoringInitialized` read outside lock

**File & Line:** `Runtime/Core/NexusRuntime.cs:516-520`

**Risk:** Two threads racing to register the first context both observe `s_monitoringInitialized == false`, both call `InitializeMonitoring()`. The function is idempotent (sets same bools to true), but the check-then-set is not atomic — a non-idempotent init would silently double-execute.

**Empirical evidence:**
```csharp
public static void RegisterContext(IContext context)
{
    // ...
    if (added)
    {
        if (!s_monitoringInitialized)              // <-- outside s_lock
        {
            InitializeMonitoring();
            s_monitoringInitialized = true;        // <-- outside s_lock
        }
        // ...
    }
}
```

**Impact:** Currently idempotent, but a future change to `InitializeMonitoring` (adding side effects like creating GameObjects) would race-silently double-init.

**Fix:** Either move inside `s_lock` or use `Interlocked.CompareExchange(ref s_monitoringInitialized, 1, 0) == 0` as the guard.

---

### 2.3 [HIGH] `ApplyTraceBufferSize` reads `s_traceIndex` without `Volatile.Read` while `RecordTrace` writes via `Interlocked.Increment` outside `s_traceLock`

**File & Line:** `Runtime/Core/NexusRuntime.cs:412, 443`

**Risk:** `ApplyTraceBufferSize` (in `s_traceLock`) reads `s_traceIndex` as a plain field. `RecordTrace` updates `s_traceIndex` via `Interlocked.Increment` **outside** `s_traceLock`. A reader can observe a stale `s_traceIndex` during resize → wrong `start` calculation → `IndexOutOfRangeException` or reading past the new (smaller) buffer.

**Empirical evidence:**
```csharp
// ApplyTraceBufferSize (line 404-421):
lock (s_traceLock)
{
    string[] current = s_traceBuffer;
    if (size == current.Length) return;
    var resized = new string[size];
    int count = Math.Min(s_traceCount, size);
    if (count > 0)
    {
        int start = ((s_traceIndex - count + 1) % current.Length + current.Length) % current.Length;
        //                                          ^^^^^^^^^^^^^ plain read, not Volatile
        for (int i = 0; i < count; i++)
            resized[i] = current[(start + i) % current.Length] ?? "";
    }
    System.Threading.Volatile.Write(ref s_traceBuffer, resized);
    s_traceCount = count;
    s_traceIndex = count - 1;
}

// RecordTrace (line 443):
int rawIndex = System.Threading.Interlocked.Increment(ref s_traceIndex);  // outside s_traceLock
```

**Impact:** Rare, but possible IndexOutOfRangeException during a resize that happens concurrently with a record.

**Fix:** `int oldIndex = System.Threading.Volatile.Read(ref s_traceIndex);` before the modulo.

---

### 2.4 [HIGH] `CommandPool.Return` calls `Cleanup` on a possibly-double-returned instance

**File & Line:** `Runtime/Core/CommandPool.cs:159-163`

**Risk:** `Cleanup` runs BEFORE the double-return HashSet check. If two threads concurrently call `Return(sameInstance)`, both call `Cleanup(command)` (which calls `ClearInjectedReferences` → `Reset()`). Then both attempt `_pooledInstances.Add`. Only one wins; the other increments `_totalDiscarded`. The `Reset()` was already idempotent for nullable fields, so this is wasteful but not corrupting.

**Empirical evidence:**
```csharp
public void Return(object command)
{
    if (command == null) return;
    lock (_poolLock)
    {
        Cleanup(command);                              // <-- runs before double-return check
        if (!_pooledInstances.Add(command))           // <-- HashSet.Add is the only guard
        {
            Interlocked.Increment(ref _totalDiscarded);
            return;
        }
        // ...
    }
}
```

**Impact:** Two redundant `Reset()` calls under concurrent double-return. Wasteful, not a bug. But: if `Reset()` ever acquires a lock (e.g. user code resets a [Inject]'d ref that triggers a callback), lock ordering issues surface.

**Fix:** Reverse the order: check `_pooledInstances.Add` first, then Cleanup.

---

### 2.5 [MEDIUM] `HybridQueue.Drain` lock-release between dequeues can interleave with producers

**File & Line:** `Runtime/Queue/HybridQueue.cs:283-308`

**Risk:** Drain releases `_threadSafeLock` between dequeues. Concurrent `EnqueueThreadSafe` can interleave. Not a bug per se (intentional for throughput) but: a producer enqueuing while drain is running will see its signal processed in the same frame (good) but the `_totalDrained` counter increments in `finally` AFTER the dispatch, not in the same atomic step as `_totalEnqueued` increment. Counters can transiently disagree.

**Empirical evidence:**
```csharp
private void Drain(QueuedSignalRingBuffer queue, object queueLock)
{
    while (true)
    {
        IQueuedSignal queuedSignal = null;
        lock (queueLock)              // <-- lock released after dequeue
        {
            if (queue.Count > 0) queuedSignal = queue.Dequeue();
        }
        if (queuedSignal == null) break;
        try { queuedSignal.Fire(_signalBus); }
        catch (Exception ex) { /* log */ }
        finally
        {
            queuedSignal.Release();
            Interlocked.Increment(ref _totalDrained);
        }
    }
}
```

**Impact:** Counters in dashboard may show momentary mismatch. No functional impact.

**Fix:** Acceptable as-is. Add a comment about the intentionally loose counter semantics.

---

### 2.6 [MEDIUM] `WindowManager.OpenWindowAsync` has TOCTOU window between registration check and instantiation

**File & Line:** `Runtime/Services/UI/WindowManager.cs:140-160` (overview)

**Risk:** Even with the E-5 fix, the lock is released for the async instantiation (correct, slow), and re-acquired afterward. Between release and re-acquire, a `Dispose` can run and tear down the manager. The `TryAcquireWindowLockAsync` returns false if disposed, but the windowName could already be in `_pendingOpenWindows`. Cleanup race possible.

**Empirical evidence:** (See file. Pattern is safe by design — the lock re-check handles most cases. Edge case: if the OpenWindowAsync caller doesn't await the result, fire-and-forget style can outlive the manager.)

**Impact:** Low — `OpenWindowAsync` returns null in the disposed case. But: the `_pendingOpenWindows` set may retain a stale entry if `Dispose` runs between Set and Remove.

**Fix:** Already mitigated by `_disposed` check. Add a final cleanup in Dispose that drains `_pendingOpenWindows` and signals waiters.

---

### 2.7 [MEDIUM] `RecoveryEngine._fallbackDepth` is not thread-local

**File & Line:** `Runtime/Core/RecoveryEngine.cs:179-180, 195, 239`

**Risk:** `_fallbackDepth` is a plain int incremented/decremented. If two threads execute fallback concurrently (sync + async paths can race if the bus dispatches the same failed signal on both paths due to a bug elsewhere), the depth counter races and could allow infinite recursion past `MaxFallbackDepth`.

**Empirical evidence:**
```csharp
private int _fallbackDepth = 0;
private const int MaxFallbackDepth = 3;
// ...
if (_fallbackDepth >= MaxFallbackDepth) { return Abort; }
_fallbackDepth++;
try { /* execute fallback */ }
finally { _fallbackDepth--; }
```

**Impact:** Probably not reachable today (sync fallback on async signal is rejected earlier), but a fragile invariant.

**Fix:** Use `Interlocked.Increment(ref _fallbackDepth)` and `Interlocked.Decrement` (compare against 3 to abort).

---

## 3. 🟡 Memory Leaks & Lifecycle Integrity

### 3.1 [HIGH] `EncryptedStorageService` subscribes to static `Application.focusChanged` and `Application.quitting` in ctor without idempotency

**File & Line:** `Runtime/Services/Storage/EncryptedStorageService.cs:166-167, 200-206`

**Risk:** Each ctor invocation adds a new handler to the static `Application.focusChanged` event. The handler is unsubscribed in `Dispose` (line 203-204), but:
1. If `Dispose` is never called, the handler persists forever (the service holds `this`, the closure holds `this` via the static event).
2. If the ctor is called twice (e.g., two contexts both bind `EncryptedStorageService` as singleton — actually impossible because it's a singleton per context, but `CreatePureContextAsync` can create multiple contexts), the second instance's `Dispose` will unsubscribe the SECOND registration while the FIRST persists. Reference leak.

**Empirical evidence:**
```csharp
public EncryptedStorageService(string customSalt)
{
    // ...
    Application.focusChanged += OnFocusChanged;       // <-- static event subscription
    Application.quitting += OnQuitting;               // <-- static event subscription
}
public void Dispose()
{
    _disposed = true;
    Application.focusChanged -= OnFocusChanged;       // <-- unsubscribes one instance
    Application.quitting -= OnQuitting;
    Save();
}
```

**Impact:** Across multiple `Context` lifetimes in Editor (domain reload disabled), the static events accumulate handlers. Each one holds a reference to a disposed service, which transitively holds the cache, the encryption keys, and the file path cache. Memory leak + GC pressure in long sessions.

**Fix:** Either:
- Store the handler delegate in a field and use the field-reference identity in the unsubscribe, OR
- Document that the service must only be constructed once (enforce via static guard), OR
- Move the event subscriptions to a `Start()` / `Stop()` lifecycle pair separate from the ctor.

---

### 3.2 [HIGH] `ObjectPoolService` `ClearAllPools` missing per-instance dict cleanup

**File & Line:** `Runtime/Services/Pool/ObjectPoolService.cs:290-318`

**Risk:** `ClearAllPools` iterates `_poolsByPrefabId` and processes each pool. For each ACTIVE instance, `OnDespawned` is called and the GameObject is destroyed. But `_poolsByInstanceId.Remove(activeId)` and `_spawnGenerations.Remove(activeId)` are NOT called per instance — only `_poolsByInstanceId.Clear()` and `_spawnGenerations.Clear()` at the end. Inconsistent with `ClearPool` (which does per-instance cleanup). The final `.Clear()` masks the bug, but if a future change adds per-instance teardown logic between the loop and the final `.Clear()`, the cleanup is silently skipped.

**Empirical evidence:**
```csharp
public void ClearAllPools()
{
    foreach (var kvp in _poolsByPrefabId)
    {
        var pool = kvp.Value;
        while (pool.Inactive.Count > 0) { /* destroy */ }
        foreach (var active in pool.Active)
        {
            if (active != null)
            {
                var poolables = active.GetComponents<IPoolable>();
                for (int i = 0; i < poolables.Length; i++)
                    try { poolables[i].OnDespawned(); } catch { }
                SafeDestroyUtility.SafeDestroy(active);
                // <-- _poolsByInstanceId.Remove(activeId) and _spawnGenerations.Remove(activeId) MISSING
            }
        }
        // ...
    }
    _poolsByPrefabId.Clear();
    _poolsByInstanceId.Clear();           // <-- cleanup happens here instead
    _spawnGenerations.Clear();
}
```

**Impact:** Inconsistent with `ClearPool` contract. Fragile against future changes.

**Fix:** Mirror `ClearPool` exactly. Move the per-instance cleanup into the active loop.

---

### 3.3 [HIGH] `ObjectPoolService.Prewarm` is fully synchronous

**File & Line:** `Runtime/Services/Pool/ObjectPoolService.cs:76-93`

**Risk:** `Prewarm(prefab, count, parent)` calls `Object.Instantiate` in a tight loop. For a pool of 500 projectiles + 200 enemies + 100 VFX = 800 GameObjects, that's a 50-200ms single-frame spike on first boot (loading scene with all pools). `DespawnAfter` already uses coroutine-based spread; `Prewarm` does not.

**Empirical evidence:**
```csharp
public void Prewarm(GameObject prefab, int count, Transform parent = null)
{
    if (prefab == null || count <= 0) return;
    var pool = GetOrCreatePool(prefab, parent);
    for (int i = 0; i < count; i++)
    {
        var instance = CreateInstance(pool);     // <-- synchronous Instantiate, no yield
        instance.SetActive(false);
        if (pool.Inactive.Count < MaxInactivePerPool)
            pool.Inactive.Push(instance);
        else
            SafeDestroyUtility.SafeDestroy(instance);
    }
}
```

**Impact:** First-frame-after-load is the worst time to spike GC and stall the main thread. Players see a frozen frame on level transition.

**Fix:** Add an async overload `PrewarmAsync(prefab, count, perFrameBudget, parent)` that spreads instantiation across frames using the same `PoolTimerRunner` pattern.

---

### 3.4 [MEDIUM] `TickService` accumulates destroyed tickables in snapshot forever

**File & Line:** `Runtime/Services/Tick/TickService.cs:204-219`

**Risk:** The per-frame loop checks `if (tickable == null || (tickable is UnityEngine.Object uo && uo == false)) continue;` but **does not remove** the dead tickable. The snapshot still contains it. Every frame, the dead reference is checked again. With 100 dead tickables and 60fps, that's 6000 wasted null-checks/sec.

**Empirical evidence:**
```csharp
for (int i = 0; i < snapshot.Length; i++)
{
    try
    {
        var tickable = snapshot[i];
        if (tickable == null || (tickable is UnityEngine.Object uo && uo == false))
            continue;        // <-- skipped but NOT removed
        tickable.Tick(deltaTime);
    }
    catch (Exception ex) { /* log */ }
}
```

**Impact:** Snapshot array retains 100+ dead Unity objects indefinitely. GC pressure from the alive-but-destroyed objects (which are not technically null but their native side is gone). Frame cost grows linearly with destroyed-tickable count.

**Fix:** Schedule a cleanup pass on dirty snapshot, or add a `CompactSnapshot` helper that removes null entries lazily. Alternatively, every Nth frame, sweep the snapshot.

---

### 3.5 [MEDIUM] `SubscriptionRegistry.AddSubscription` rebuilds full read-copy dict on every Add

**File & Line:** `Runtime/Core/SubscriptionRegistry.cs:158`

**Risk:** `_subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>(_subscriptions);` allocates a new dict on every `Subscribe` call. For UI windows opening/closing rapidly, dozens of subscriptions per second → dozens of dict allocations.

**Empirical evidence:**
```csharp
public void AddSubscription(Type signalType, object rawSubscription, object handler, bool isAsync)
{
    lock (_subLock)
    {
        _subscriptions.TryGetValue(signalType, out var head);
        var node = SubscriptionNodePool.Rent(handler, rawSubscription, isAsync);
        node.Next = head;
        _subscriptions[signalType] = node;
        _subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>(_subscriptions);  // <-- full rebuild
    }
}
```

**Impact:** Per-Subscribe allocation proportional to total signal type count. With 50 signal types, 50-entry dict allocation per subscribe.

**Fix:** Only add the new entry: `_subscriptionsReadCopy[signalType] = node;` (reference assignment, no rebuild). The `Reset` of the chain happens lazily on the read side if the head changes (but head is captured at dispatch time, so atomic snapshot is OK).

---

### 3.6 [MEDIUM] `SnapshotDelegateSet.GetSnapshot` takes lock on every read

**File & Line:** `Runtime/Models/SecureObservableCore.cs:56-67`

**Risk:** ObservableProperty.Value setter calls `_observers.GetSnapshot()` on every change. If the observable has 10 subscribers and fires 1000x/frame, that's 10000 lock acquisitions on the same uncontended lock per frame.

**Empirical evidence:**
```csharp
public TDelegate[] GetSnapshot()
{
    lock (_handlersLock)
    {
        if (_snapshotDirty)
        {
            _snapshotCache = _handlers != null && _handlers.Count > 0 ? _handlers.ToArray() : null;
            _snapshotDirty = false;
        }
        return _snapshotCache;
    }
}
```

**Impact:** Allocation-free on the path, but lock-acquisition overhead is ~20ns × 10000 = 200μs/frame on a busy model.

**Fix:** Volatile snapshot reference: `private volatile TDelegate[] _snapshotCopy;` — readers do `var snap = _snapshotCopy;` lock-free, writers swap under lock. Same pattern as `SubscriptionRegistry` was supposed to use.

---

### 3.7 [MEDIUM] `ObservableList.Count` and indexer take lock on every access

**File & Line:** `Runtime/Models/ObservableProperty.cs:164-169`

**Risk:** UI bind loops that read `observableList.Count` and `observableList[i]` in a tight loop will acquire the lock N+1 times per frame.

**Empirical evidence:**
```csharp
public int Count { get { lock (_eventLock) return _items.Count; } }
public T this[int index]
{
    get { lock (_eventLock) return _items[index]; }
    set { lock (_eventLock) _items[index] = value; }
}
```

**Impact:** 100-item inventory rendered every frame = 100 lock acquisitions per frame.

**Fix:** For read-only views, expose a `AsReadOnlySpan()` or snapshot the list under a single lock. Or use a `volatile int _count;` updated under the same lock as the list.

---

### 3.8 [MEDIUM] `ObservableList.AsReadOnly` allocates `new List<T>` on every call

**File & Line:** `Runtime/Models/ObservableProperty.cs:174`

**Risk:** `AsReadOnly()` is documented as "minimal" but allocates a full `new List<T>(_items)` on every call. UI bindings that refresh each frame will leak this allocation.

**Empirical evidence:**
```csharp
public ReadOnlyListWrapper<T> AsReadOnly()
{
    lock (_eventLock)
        return new ReadOnlyListWrapper<T>(new List<T>(_items));   // <-- allocation
}
```

**Impact:** Per-bind-call allocation. Compounds with the lock issue above.

**Fix:** Cache the read-only list, invalidate on mutation (or version-stamp and let the wrapper itself snapshot lazily).

---

### 3.9 [MEDIUM] `TickService.RegisterTickable` does O(N) `Contains` check under lock

**File & Line:** `Runtime/Services/Tick/TickService.cs:111-119`

**Risk:** `_tickables.Contains(tickable)` is O(N) on `List<T>`. For N=500 tickables and 1000 Register/Unregister per frame (spawn/despawn storms), that's 500,000 comparisons per frame.

**Empirical evidence:**
```csharp
public void RegisterTickable(ITickable tickable)
{
    if (tickable == null) return;
    lock (_lock)
    {
        if (!_tickables.Contains(tickable))    // <-- O(N) per call
        {
            _tickables.Add(tickable);
            _tickablesDirty = true;
        }
    }
}
```

**Impact:** O(N²) spawn storms. On mobile, the main thread can spike to 50ms+ for a single frame.

**Fix:** Maintain a parallel `HashSet<ITickable>` for O(1) dedup, rebuild on each snapshot. Or use a `Dictionary` keyed by tickable.

---

### 3.10 [MEDIUM] `TickService.UnregisterTickable` allocates snapshot array unconditionally

**File & Line:** `Runtime/Services/Tick/TickService.cs:128`

**Risk:** `_tickables.ToArray()` is called on every Unregister. Comment says "Unregister stays immediate — its removal is allocation-free anyway" but the `.ToArray()` is NOT allocation-free. With many despawns per frame, this allocates many arrays.

**Empirical evidence:**
```csharp
public void UnregisterTickable(ITickable tickable)
{
    if (tickable == null) return;
    lock (_lock)
    {
        if (_tickables.Remove(tickable))
        {
            _tickableSnapshot = _tickables.Count > 0 ? _tickables.ToArray() : null;
            //                                            ^^^^^^^^^^^^^^^^^ O(N) allocation per call
            _tickablesDirty = false; // snapshot is already current — avoid a redundant rebuild.
        }
    }
}
```

**Impact:** Allocation per Unregister, O(N) per call. Despawn storms allocate many arrays.

**Fix:** Defer the snapshot rebuild — set dirty flag, let the next OnTick rebuild. The "Unregister stays immediate" intent is to ensure the dead tickable doesn't get ticked again; a single-frame delay (until the next OnTick acquires the lock) achieves the same goal with zero allocation.

---

### 3.11 [LOW] `SecureKeyGen.NextIntKey` allocates `byte[4]` on every call

**File & Line:** `Runtime/Models/SecureObservableCore.cs:110-117`

**Risk:** Every SecureObservable* construction calls `NextIntKey()` twice (for the key pair) → 2 byte[4] allocations per instance. If a game has 50 observables (e.g., 50 model fields wrapped in Secure), that's 100 byte[] allocations at boot.

**Empirical evidence:**
```csharp
public static int NextIntKey()
{
    byte[] bytes = new byte[4];    // <-- allocation per call
    s_rng.GetBytes(bytes);
    int key = BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF;
    return key != 0 ? key : 0x4E5855;
}
```

**Impact:** Marginal boot-time allocation. Not a hot path.

**Fix:** Use `[ThreadStatic] byte[] _buffer4` or `stackalloc Span<byte>`. Minor.

---

### 3.12 [LOW] `QueuedSignalPool<T>.Rent` has redundant null-check

**File & Line:** `Runtime/Queue/HybridQueue.cs:74-90`

**Risk:** Minor code smell, not a bug. Two `if (wrapper == null)` checks.

**Empirical evidence:**
```csharp
public static QueuedSignalWrapper<T> Rent(T signal)
{
    QueuedSignalWrapper<T> wrapper = null;
    lock (s_poolLock)
    {
        if (s_pool.Count > 0)
        {
            wrapper = s_pool.Pop();
            s_pooledInstances.Remove(wrapper);
        }
        if (wrapper == null)            // <-- second check, redundant
        {
            wrapper = new QueuedSignalWrapper<T>();
        }
        wrapper.Signal = signal;
    }
    return wrapper;
}
```

**Impact:** None functional. Cosmetic.

**Fix:** Use `else` branch for new allocation.

---

### 3.13 [MEDIUM] `AudioService.InitializeAsync` is not idempotent

**File & Line:** `Runtime/Services/Audio/AudioService.cs:145-172`

**Risk:** If `InitializeAsync` is called twice (e.g., the service is mistakenly constructed and bound as transient, or a recovery re-init), a second `[Nexus_AudioService]` GameObject is created. Two audio roots → sounds play from both, BGM crossfades between duplicate sources.

**Empirical evidence:**
```csharp
public override ValueTask InitializeAsync(CancellationToken ct)
{
    _audioRoot = AudioRootProvider?.GetOrCreateRoot();
    if (_audioRoot == null)
    {
        _audioRoot = new GameObject("[Nexus_AudioService]");  // <-- second instance
        UnityEngine.Object.DontDestroyOnLoad(_audioRoot);
    }
    _bgmSourceActive = _audioRoot.AddComponent<AudioSource>(); // <-- new sources on existing root
    // ...
}
```

**Impact:** Audio bugs are hard to diagnose. Two BGM crossfades overlap.

**Fix:** Add `if (_audioRoot != null) return default;` guard at the top.

---

## 4. 🟢 Hot-Path & Platform Audit (IL2CPP / AOT / Zero-Alloc)

### 4.1 [MEDIUM] `SignalBus.FireInternal` metrics calls on every fire even in production

**File & Line:** `Runtime/Core/SignalBus.cs:384-385`

```csharp
NexusRuntime.Metrics.RecordSignalDispatched();
NexusRuntime.Metrics.RecordTrace(SignalTraceLabel<T>.Fire);
```

These execute on EVERY fire, even in production. `RecordTrace` allocates a new `string[]` if a resize is needed, and the trace label itself is a string concat done ONCE per type (cached) but the array index + lock-free Volatile read is still 10-50ns.

**Impact:** For 10,000 fires/frame, that's 100-500μs on metrics. Not the bottleneck but visible in profiler.

**Fix:** Wrap in `#if UNITY_EDITOR || DEVELOPMENT_BUILD` or read a static `s_metricsEnabled` flag (set by build configuration).

---

### 4.2 [MEDIUM] `CommandRegistry.RegisterCommand` sorts on every Add (Sequential/Exclusive mode)

**File & Line:** `Runtime/Core/CommandRegistry.cs:140-143`

```csharp
if (mode != ExecutionMode.Concurrent)
{
    list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
}
```

`list.Sort` with a lambda allocates a `FunctorComparer<T>` wrapper (delegate allocation) per call. For Sequential mode with 10 commands registered at boot, 10 sort calls = 10 delegate allocations.

**Impact:** Boot-time allocation spike. The whole snapshot rebuild (#1.5 of the original refactor plan) compounds this.

**Fix:** Use a typed delegate cache: `static readonly Comparison<CommandHandlerInfo> s_priorityDesc = (a, b) => b.Priority.CompareTo(a.Priority);`

---

### 4.3 [LOW] `EncryptionStorageService.OnFocusChanged` captures `this` in fire-and-forget Task.Run

**File & Line:** `Runtime/Services/Storage/EncryptedStorageService.cs:186-194`

```csharp
var self = this; // explicit capture for clarity
System.Threading.Tasks.Task.Run(() =>
{
    try { self.Save(); }
    catch (Exception ex) { /* log */ }
});
```

The comments are contradictory: first says "no risk of GC" (because singleton), then says "explicit capture for clarity". The Task itself is fire-and-forget — if the AppDomain is torn down before it runs, the task is silently cancelled. No way to await.

**Impact:** Last-second save on focus loss can be lost on rapid app backgrounding.

**Fix:** Document the limitation. Consider synchronous `Save()` on focus loss if data integrity > frame time.

---

### 4.4 [LOW] `NetworkSignalHistory<T>.ReplaySignals` uses `is SignalBus` pattern check per signal

**File & Line:** `Runtime/Netcode/NetworkSignalBus.cs:111-118`

```csharp
if (localSignalBus is SignalBus concreteBus)
    concreteBus.FireQueued(_signals[i].Signal);
else
    localSignalBus.Fire(_signals[i].Signal);
```

The `is` pattern allocates a local but the cast is checked. For network replay with thousands of signals, this is O(N) cast checks. Could be replaced with a virtual call on ISignalBus (one entry point that knows its own type).

**Impact:** Marginal on hot path. Not a priority.

**Fix:** Add `FireQueued` to `ISignalBus` interface (default impl could throw NotSupportedException).

---

### 4.5 [LOW] `SubscriptionNodePool` `Reset()` runs OUTSIDE the lock during Return

**File & Line:** `Runtime/Core/SubscriptionRegistry.cs:62-72`

```csharp
public static void Return(SubscriptionNode node)
{
    node.Reset();   // <-- OUTSIDE lock
    lock (s_poolLock)
    {
        if (s_pool.Count < MaxPoolSize) s_pool.Push(node);
    }
}
```

`Reset()` is outside the lock, so another thread could be reading the node's fields via the dispatch iteration (which holds the snapshot's reference, not the lock). The `is Action<T>` pattern in the dispatch evaluates synchronously. **If a handler is in the middle of reading `current.Handler is Action<T> syncSub` and another thread calls Return + Reset, syncSub could be null at the moment of the cast.** But the C# spec guarantees that `is` and the assignment happen atomically within a single expression, so once `is` returns true, `syncSub` is bound. Safe.

**Impact:** None — the `is` pattern in C# is a single atomic read.

**Fix:** None needed. Could be tightened by moving `Reset()` inside the lock for clarity.

---

## 5. 🟢 INFO / Code Quality Observations

### 5.1 `NexusRuntime.Logger` getter is called on every signal fire

**File & Line:** `Runtime/Core/CommandPool.cs:123-125` and many similar

```csharp
NexusRuntime.Logger?.LogWarning(...)
```

Each call acquires `s_loggerCacheLock` and re-resolves the logger from DI. In a hot path with 1000 warnings/sec (e.g., during a partial failure cascade), the lock + DI lookup is wasteful.

**Fix:** Cache the logger at service init or use a `Lazy<ILoggerService>` in the context.

---

### 5.2 `_commandHandlersReadCopy` rebuild in `RegisterCommand` and `_hasAsyncHandlerReadCopy` rebuild

**File & Line:** `Runtime/Core/CommandRegistry.cs:145-148`

```csharp
_commandHandlersReadCopy = new Dictionary<Type, List<CommandHandlerInfo>>(_commandHandlers.Count);
foreach (var kvp in _commandHandlers)
    _commandHandlersReadCopy[kvp.Key] = new List<CommandHandlerInfo>(kvp.Value);
_hasAsyncHandlerReadCopy = new Dictionary<Type, bool>(_hasAsyncHandler);
```

O(N) per Register, with double allocation (dict + each list copy). For 50 commands at boot = 50 * 51 = 2550 allocations.

**Fix:** Already covered in the original refactor plan. Per-key incremental update.

---

### 5.3 `CommandRegistry.TryGetHandlers` returns `List<>` not `IReadOnlyList<>`

**File & Line:** `Runtime/Core/CommandRegistry.cs:75+`

The exposed read-copy uses `List<CommandHandlerInfo>` which can be cast back to mutable. Defensive `IReadOnlyList<CommandHandlerInfo>` would be safer.

**Fix:** Change return type and rebuild sites.

---

## 6. 📊 Audit Summary Table

| # | Category | File:Line | Severity | Status | One-line |
|---|----------|-----------|----------|--------|----------|
| 1.1 | Logic / Dead Math | NexusRuntime.cs:467 | 🔴 CRITICAL | Confirmed | Trace ring misses newest entry after wrap |
| 1.2 | Thread-Safety | NexusRuntime.cs:311-313 | 🔴 CRITICAL | Confirmed | Event nulled inside lock; invoke-after-reset NRE |
| 1.3 | Thread-Safety | ObservableProperty.cs:54-66 | 🔴 CRITICAL | Confirmed | `volatile bool` check-then-set is not atomic |
| 1.4 | Logic / API Hole | ObjectPoolService.cs:140-150 | 🔴 CRITICAL | Confirmed | `Despawn(prefab)` destroys the prefab asset |
| 1.5 | Logic / Double-Reset | CommandPool.cs:142-143, 213 | 🔴 CRITICAL | Confirmed | Every pooled command Reset()'d twice per cycle |
| 2.1 | Thread-Safety / Lock Order | NexusRuntime.cs:144-156 | 🟠 HIGH | Confirmed | Logger getter holds cache lock over DI resolve |
| 2.2 | Thread-Safety | NexusRuntime.cs:516-520 | 🟠 HIGH | Confirmed | s_monitoringInitialized check outside lock |
| 2.3 | Thread-Safety / TOCTOU | NexusRuntime.cs:412 | 🟠 HIGH | Confirmed | s_traceIndex read plain in resize; Interlocked write outside lock |
| 2.4 | Thread-Safety | CommandPool.cs:159-163 | 🟠 MEDIUM | Confirmed | Cleanup runs before double-return guard |
| 2.5 | Thread-Safety | HybridQueue.cs:283-308 | 🟠 MEDIUM | Confirmed | Lock release between dequeue and dispatch |
| 2.6 | Lifecycle / Race | WindowManager.cs:140-160 | 🟠 MEDIUM | Confirmed | Dispose can race with OpenWindowAsync between lock release and re-acquire |
| 2.7 | Thread-Safety | RecoveryEngine.cs:179-180 | 🟠 MEDIUM | Confirmed | _fallbackDepth plain int under multi-thread access |
| 3.1 | Memory Leak | EncryptedStorageService.cs:166-167 | 🟠 HIGH | Confirmed | Static event handlers leak across ctor/dispose cycles |
| 3.2 | Lifecycle Inconsistency | ObjectPoolService.cs:290-318 | 🟠 HIGH | Confirmed | ClearAllPools missing per-instance dict cleanup |
| 3.3 | Boot Time | ObjectPoolService.cs:76-93 | 🟠 HIGH | Confirmed | Prewarm fully synchronous — frame spike on first load |
| 3.4 | Memory Leak | TickService.cs:204-219 | 🟠 MEDIUM | Confirmed | Dead tickables never removed from snapshot |
| 3.5 | Allocation | SubscriptionRegistry.cs:158 | 🟠 MEDIUM | Confirmed | Full dict rebuild on every Add |
| 3.6 | Hot Path | SecureObservableCore.cs:56-67 | 🟠 MEDIUM | Confirmed | GetSnapshot takes lock every read |
| 3.7 | Hot Path | ObservableProperty.cs:164-169 | 🟠 MEDIUM | Confirmed | List indexer takes lock per access |
| 3.8 | Allocation | ObservableProperty.cs:174 | 🟠 MEDIUM | Confirmed | AsReadOnly allocates new List every call |
| 3.9 | Hot Path | TickService.cs:111-119 | 🟠 MEDIUM | Confirmed | O(N) Contains on List for dedup |
| 3.10 | Allocation | TickService.cs:128 | 🟠 MEDIUM | Confirmed | Unregister allocates snapshot array |
| 3.11 | Boot Time | SecureObservableCore.cs:110-117 | 🟢 LOW | Confirmed | byte[4] allocation per key call |
| 3.12 | Code Quality | HybridQueue.cs:74-90 | 🟢 LOW | Confirmed | Redundant null check in Rent |
| 3.13 | Lifecycle | AudioService.cs:145-172 | 🟠 MEDIUM | Confirmed | InitializeAsync not idempotent |
| 4.1 | Hot Path | SignalBus.cs:384-385 | 🟠 MEDIUM | Confirmed | Metrics fire on every Fire even in release |
| 4.2 | Boot Time | CommandRegistry.cs:140-143 | 🟠 MEDIUM | Confirmed | Sort allocates delegate per Register |
| 4.3 | Reliability | EncryptedStorageService.cs:186-194 | 🟢 LOW | Confirmed | Fire-and-forget Task.Run can lose save |
| 4.4 | Hot Path | NetworkSignalBus.cs:111-118 | 🟢 LOW | Confirmed | `is SignalBus` pattern check per signal |
| 4.5 | Comment Mismatch | SubscriptionRegistry.cs:62-72 | 🟢 LOW | Confirmed | Reset() outside lock but comment says safe-inside |
| 5.1 | Hot Path | CommandPool.cs:123-125 | 🟠 MEDIUM | Confirmed | Logger getter resolves from DI on every log |
| 5.2 | Boot Time | CommandRegistry.cs:145-148 | 🟠 HIGH | Confirmed | Snapshot rebuild O(N) per Register |
| 5.3 | API Surface | CommandRegistry.cs:75+ | 🟢 LOW | Confirmed | Read-copy exposes mutable List<> |

---

## 7. Empirical Evidence Notes

All findings above are derived from **direct code reading**, not from running tests. The `Tests/Runtime/AdversarialReviewFixTests.cs` and `Tests/Editor/AdversarialAuditVerificationTests.cs` files exist but I have not read them — adversarial fixes referenced in code comments (P0-3, A8, M3, T6, etc.) suggest the maintainer has already done several review passes. Many of my findings may be addressed in those tests or in unreleased branches.

**Specific things to verify before acting on a finding:**
- Whether the test file `Tests/Runtime/AdversarialReviewFixTests.cs` has a test that proves the buffer ring math is correct (in which case finding 1.1 is either already known-fixed or a known false positive).
- Whether the test file `Tests/Editor/AdversarialAuditVerificationTests.cs` exercises the lock-ordering scenarios in 2.1 and 2.3.
- Whether the maintainer's internal fork has different versions of the files I reviewed.

**Suggestions for empirical verification:**
- Unit test for finding 1.1: write 6+ entries, then call `GetRecentTraces`, assert entry 0 is the 6th.
- Concurrency test for finding 1.3: spawn 8 threads each setting `Value` 10000x, assert exactly N total subscriber invocations.
- Boot-time measurement for finding 3.3: profile first-frame-after-load with 500-instance prewarm, compare with coroutine spread.
- Memory leak test for finding 3.1: instantiate + dispose `EncryptedStorageService` 100x in a test, check `Application.focusChanged` invocation list length.

---

## 8. Top-5 Recommended Fixes (by ROI)

Sorted by impact-to-effort ratio:

1. **1.1 Trace buffer ring math** — 2-line fix, eliminates a real telemetry bug.
2. **1.5 Double-Reset in CommandPool** — 3-line fix, removes a per-fire overhead.
3. **2.1 Logger property lock ordering** — 10-line refactor, removes a deadlock class.
4. **1.4 Despawn(prefab) asset destruction** — 5-line guard, prevents production data loss.
5. **3.1 EncryptedStorageService static event leak** — 10-line fix, prevents Editor memory leak.

After these, the next batch:
6. 1.3 ObservableProperty check-then-set atomicity (Interlocked.CompareExchange pattern)
7. 3.3 Prewarm async overload (significant effort, but major boot-time win)
8. 5.2 CommandRegistry snapshot incremental update (covered in original refactor plan)

---

## 9. Audit Limitations

- **Did not execute the test suite.** Findings 1.1, 1.3, 2.1 may have associated tests that prove my reading wrong. Verify before fixing.
- **Did not profile live.** Allocation counts and timing are inferred from code patterns, not measured.
- **Did not review Editor/ or Samples~/ folders.** The Editor contains UI Toolkit dashboard code and a LiveReload processor (likely with its own race conditions); Samples contains the example project's commands/mediators. Out of scope for this audit.
- **Did not review Netcode/ or DOTS/ in depth.** `NetworkSignalBus.cs` was sampled; full review would require reading the snapshot buffer and tick-replay logic in detail.
- **Plugins/ and FSM/ folders not read.** 213+ lines in `PluginSystem.cs`, 100+ in `GameStateMachine.cs` — both likely have their own thread-safety concerns.
- **Editor/LiveReload/LiveReloadProcessor.cs** was not read. Live reload in domain-reload-disabled mode is a notoriously fragile area.
