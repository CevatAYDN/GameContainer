# ADR-0002: Context Facade — Reviewed, No Deepening Needed

- **Status:** Accepted (review closure)
- **Date:** 2026-08-01

## Context

A 2026-08-01 architecture review candidate asked whether the `Context` class is a
shallow facade that should be deepened. At the time it was deferred ("wait for
candidates 1–2" — the SignalBus execution/recovery extraction).

## Assessment (post-extraction)

After the CommandExecutor/RecoveryEngine extraction, `Context`'s delegating surface is
thin and coherent:

- `Resolve`/`TryResolve` → NexusDI (correct facade delegation).
- `RegisterView`/`UnregisterView` → ViewBinder (correct facade delegation).
- Plugin registry uses a lock-free read-only copy for snapshot safety.
- Disposal order is hardened (INexusService orphans owned by the context, reverse-order
  lifecycle disposal, leak-free soak verified by the harness).

**Deletion test:** removing the facade would scatter ownership across callers, not
concentrate it — the facade is at the right depth. No further work.

## Decision

Close the candidate as **no action**. Do not re-propose a Context-facade deepening in
future reviews without new evidence (e.g. a caller-count explosion or a new sub-module
that turns the facade into a pass-through chain).

## References

- 2026-08-01 architecture review — candidate 5 (Context facade).
