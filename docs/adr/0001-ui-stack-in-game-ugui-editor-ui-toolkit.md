# ADR-0001: UI Stack — In-Game UGUI, Editor UI Toolkit

- **Status:** Accepted
- **Date:** 2026-08-01
- **Decision makers:** Project owner

## Context

The Nexus framework's window layer (`WindowManager`, `IUIAssetProvider`) is built on
Unity's legacy GameObject/UGUI pipeline and exposes `Task<GameObject>` from its public
interface. A prior architecture review flagged the `GameObject` leak as a candidate for
an opaque `IWindowView` abstraction, but explicitly deferred the decision until the UI
technology direction was settled.

The project owner has now decided the UI direction:

- **In-game runtime UI:** UGUI (the existing `WindowManager` pipeline stays).
- **Editor screens and tooling:** UI Toolkit (separate editor-side surface, not the
  runtime `WindowManager`).

## Decision

1. The in-game runtime window system remains **UGUI/GameObject-based**. `Task<GameObject>`
   is the *correct* in-game abstraction for UGUI — `IWindowView` is **not** introduced
   (one adapter, UGUI, is a hypothetical seam; with UGUI fixed as the in-game tech the
   abstraction would add indirection without a second adapter to justify it).
2. Editor-side screens use **UI Toolkit** in the `Editor` assembly, separate from the
   runtime window manager.
3. The runtime `WindowManager` is deepened instead of abstracted: canvas root creation,
   per-layer transforms, and modal interactivity policy moved into a dedicated
   `UICanvasSystem` module (2026-08-01), so the manager is a pure window-lifecycle
   orchestrator and the UGUI canvas policy lives in exactly one place.

## Consequences

- No public API break in the runtime window layer.
- If the in-game stack ever migrates to UI Toolkit, `WindowManager` + `UICanvasSystem`
  are the two files to replace; the lifecycle/interactivity tests are the migration
  contract.
- Future architecture reviews should not re-flag the `Task<GameObject>` interface as a
  deepening candidate while UGUI is the in-game stack.

## Addendum (2026-08-06) — UIManager as the forward API

Records the current codebase state (does not change this ADR's technology decision — the
in-game stack stays UGUI):

- `UIManager` (typed `ScreenView`-based API with pooling, `UICanvasSystem` layer policy)
  has been introduced as the **forward-looking** runtime screen API.
- `WindowManager` is marked `[Obsolete]` — "use IUIManager; kept for backward
  compatibility, removed in a future major version". Existing string-keyed windows
  (including the demo screens, which derive from `View`, not `ScreenView`) continue to
  run on `WindowManager`; migration is gradual.
- The analyzer rule NEXUS004 flags new `WindowManager` references to drive the migration.

A future review may revisit this addendum if the migration completes or the direction
changes; this ADR's original decision (UGUI stays, `Task<GameObject>` stays in service)
remains in effect.

## Addendum (2026-08-06, second) — WindowManager removed; UIManager is the single UI manager

The migration this ADR's first addendum recorded is **complete**:

- `WindowManager` + `IWindowManager` are **deleted**. `UIManager` is the single runtime
  UI manager; `UILayer` and `IUIWindowLifecycle` were extracted from `WindowManager.cs`
  into their own files so the canonical stack keeps compiling.
- The demo screens (`MainMenuScreen`, `GameplayHUD`, `GameOverScreen`, `ShopScreen`)
  now derive from `ScreenView` and are opened through `IUIManager.OpenScreenAsync<TScreen>`.
  The demo binds one UI manager only — no parallel window stack remains.
- `CasualServicesPlugin`'s legacy window panel now drives `UIManager`'s editor
  introspection (`GetOpenScreensSnapshot`/`PendingScreenCount`).
- The analyzer rule NEXUS004 (migration driver) is **retired** together with its editor
  tests — there is no obsolete API left to police.
- The benchmark's WindowManager W1–W7 proof was migrated to UIManager U1–U7.

This ADR's original decision (in-game UGUI stays; `Task<GameObject>` as the in-game
abstraction) is unchanged — the consolidation happened on top of it.

## References

- 2026-08-01 architecture review — candidate 4 (WindowManager).
- 2026-08-01 refactor — `UICanvasSystem.cs` extraction.
