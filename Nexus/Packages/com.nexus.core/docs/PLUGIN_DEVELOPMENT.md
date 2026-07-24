> **For AI Agents:** This document is the source of truth for developing, refactoring, and auditing editor plugins in the Nexus framework. Before making plugin edits:
> 1. Verify `INexusEditorPlugin` contract requirements.
> 2. Ensure `OnUpdate()` is overridden for polling — **DO NOT** use `_view.schedule`.
> 3. Run plugin tests: `PluginRefactorValidationTests`.
>
> **Out of scope:** Runtime game logic, runtime SignalBus implementation.

# Nexus Plugin Development Guide

This document outlines the architecture, coding standards, lifecycle rules, anti-patterns, and edge cases for developing editor plugins within the **Nexus Architecture Suite**.

---

## 📋 The `INexusEditorPlugin` Interface Contract

Every plugin in Nexus extends `NexusEditorPlugin` base class (which implements `INexusEditorPlugin`):

```csharp
public interface INexusEditorPlugin
{
    string Id { get; }
    string DisplayName { get; }
    int Order { get; }
    void Initialize(NexusWindow window);
    VisualElement CreateView();
    void OnEnable();
    void OnDisable();
    void OnUpdate();
    IReadOnlyList<(string Label, Action Action, Color Color)> GetContextActions();
}
```

---

## 📋 New Plugin Onboarding Checklist

Before submitting a new plugin or refactor, verify:

- [ ] Extends `NexusEditorPlugin` (not just bare `INexusEditorPlugin`).
- [ ] `Id` is unique across all 15 plugins (e.g. `"Dashboard"`, `"Tracer"`, `"GameManager"`).
- [ ] `DisplayName` uses `NexusLang.Get("key")` for localization.
- [ ] `Order` is specified (e.g. 0 for Dashboard, 1 for Wizard, 6 for GameManager).
- [ ] `CreateView()` returns a non-null `VisualElement` tree.
- [ ] **`OnUpdate()` is overridden** for periodic data polling — **NEVER** use `_view.schedule` or `EditorApplication.update`.
- [ ] `OnDisable()` resets: instance fields, flags, counters, queues.
- [ ] All user-facing text uses `NexusLang.Get("key")` — zero hardcoded English strings.
- [ ] Stat displays use `NexusEditorStyles.CreateStatTile(...)` — no custom stat components.
- [ ] Reflection is cached (`MethodInfo` dictionary, static catalog with `[DidReloadScripts]`).
- [ ] Empty `catch {}` blocks are NOT present (log `ReflectionTypeLoadException.LoaderExceptions`).
- [ ] At least 1 NUnit test added under `Tests/Editor/PluginRefactorValidationTests.cs`.
- [ ] `LOCALIZATION_KEYS.md` updated with any new keys.
- [ ] `CHANGELOG.md` updated under `[Unreleased]`.

---

## ⚠️ Edge Cases Catalog

| Edge Case Scenario | Required Behavior & Implementation |
|---|---|
| **Window hidden / tab inactive** | `OnUpdate()` is driven by `NexusWindow` only for active tabs. Hidden tabs do not consume CPU. |
| **Domain reload (script recompile)** | Static caches survive reload. Use `[DidReloadScripts]` attribute to clear static caches (`s_typedCatalog.Clear()`). |
| **Play Mode entry** | Subscribe to `EditorApplication.playModeStateChanged` in `CreateView()` / `OnEnable()`. |
| **Play Mode exit** | Unsubscribe from `EditorApplication.playModeStateChanged` in `OnDisable()`. Reset live runtime metrics. |
| **Context disposed during update** | Null-check active context references (`if (ctx == null) continue;`) before dereferencing containers. |
| **`OnDisable` called before `OnEnable`** | `OnDisable()` must be defensive and null-check all references (`_debounce?.Pause()`). |
| **Tab opened multiple times** | Each `CreateView()` invocation creates a new tree. Instance fields store view elements cleanly. |
| **Locale switched at runtime** | Text relies on `NexusLang.Get()`. Re-render view on language change if needed. |
| **No active context (Edit Mode)** | Show a clean `NexusEditorStyles.CreateEmptyState` panel with an "Enter Play Mode" hint. |
| **Rapid typing in text fields** | Debounce search inputs by 200ms using `_view.schedule.Execute(...).StartingIn(200)`. |
| **Empty or null data collections** | Render `CreateEmptyState()`, do not throw `NullReferenceException` or leave blank panel. |

---

## 🚫 Anti-Pattern Callouts

### ❌ Anti-Pattern 1: Custom UI Schedulers (`_view.schedule`)
```csharp
// BAD:
_refreshSchedule = _view.schedule.Execute(RefreshStats).Every(500);

// GOOD:
public override void OnUpdate()
{
    base.OnUpdate();
    RefreshStats();
}
```
*Why:* Custom schedules continue firing when the editor window tab is hidden or minimized, consuming background CPU. The host `NexusWindow` automatically invokes `OnUpdate()` on the active tab every ~200ms.

### ❌ Anti-Pattern 2: Retaining State Across `OnDisable()`
```csharp
// BAD:
public override void OnDisable()
{
    base.OnDisable();
    // Flags and queue retain stale state
}

// GOOD:
public override void OnDisable()
{
    _hasLiveEvents = false;
    _productionTraceFrameCounter = 0;
    while (_incomingEvents.TryDequeue(out _)) {}
    EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
    base.OnDisable();
}
```
*Why:* Leaving stale queue items or flags active causes the first update after re-enabling the tab to execute with outdated data.

### ❌ Anti-Pattern 3: Rebuilding `ScrollView` Elements on Tick
```csharp
// BAD:
_scrollView.Clear();
foreach (var ev in items) _scrollView.Add(CreateItemElement(ev));

// GOOD:
_listView = new ListView
{
    fixedItemHeight = 28f,
    selectionType = SelectionType.Single,
    makeItem = () => new CustomItemElement(),
    bindItem = (el, i) => ((CustomItemElement)el).Bind(_items[i])
};
```
*Why:* Allocating hundreds of `VisualElement` nodes every 100-200ms causes heavy GC pressure and loses user scroll position. `ListView` virtualization recycles elements.

### ❌ Anti-Pattern 4: Hardcoded User-Facing Strings
```csharp
// BAD:
var button = new Button { text = "Copy" };

// GOOD:
var button = new Button { text = NexusLang.Get("dash_qf_copy") };
```
*Why:* Hardcoded English strings break localization support for Turkish and future languages.

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Framework overview, file map, and decision flows
- 🏛️ [ARCHITECTURE.md](ARCHITECTURE.md) — Runtime architecture and lifecycle sequence
- 🌐 [LOCALIZATION_KEYS.md](LOCALIZATION_KEYS.md) — Active localization keys catalog
- 🤝 [CONTRIBUTING.md](CONTRIBUTING.md) — Pull request rules and AI checklist

---

**Last updated:** 2026-07-24  
**Code version:** 0.4.0  
**Maintainers:** Nexus Core Team  
**Re-review trigger:** Any change to `INexusEditorPlugin.cs` or `Editor/Plugins/`.
