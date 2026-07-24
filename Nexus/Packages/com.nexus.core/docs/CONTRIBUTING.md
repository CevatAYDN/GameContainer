> **For AI Agents:** This document is the source of truth for contribution standards, PR verification rules, and automated test enforcement. Before submitting a pull request:
> 1. Complete the [PR Review Checklist](#pr-review-checklist-for-ai-agents) below.
> 2. Run all NUnit tests: EditMode `PluginRefactorValidationTests` and `InfrastructureValidationTests`.
> 3. Update `CHANGELOG.md` under `[Unreleased]`.
>
> **Out of scope:** Non-framework project files outside `GameContainer/Nexus/Packages/com.nexus.core`.

# Contributing to Nexus Core

Thank you for contributing to Nexus Core! Please follow these guidelines when submitting bug fixes, features, refactors, or documentation updates.

---

## 🛠️ Development & Coding Rules

### 1. Cross-Cutting Plugin Rules
All editor plugin changes must follow the 4 cross-cutting patterns:
- **`OnUpdate()` Override**: Do NOT use `_view.schedule` or custom update timers. Override `INexusEditorPlugin.OnUpdate()`.
- **State Reset in `OnDisable()`**: Reset all flags, queues, and debounces when the plugin is hidden.
- **Reflection Caching**: Cache `MethodInfo` lookups and assembly scan lists (`[DidReloadScripts]`).
- **No Empty Catches**: Log `ReflectionTypeLoadException.LoaderExceptions` using `Debug.LogWarning`.

### 2. Localization Policy
- Nexus supports **English (`en`)** and **Turkish (`tr`)**.
- Use `NexusLang.Get("key")` for all user-facing UI text.
- Do NOT hardcode string interpolations for count words (e.g. `$"{count} models"`).

### 3. Automated Testing Requirements
- Every P1/P2 fix or new plugin MUST include unit test coverage under `Tests/Editor/`.
- Verify that both `InfrastructureValidationTests` and `PluginRefactorValidationTests` pass cleanly in Unity Test Runner.

---

## 📋 PR Review Checklist for AI Agents

When reviewing or self-auditing a PR, validate every check item:

### Code Quality & Architecture
- [ ] No `_view.schedule`, `_root.schedule`, or `EditorApplication.update` calls in plugins.
- [ ] `OnUpdate()` properly overridden if periodic polling is needed.
- [ ] `OnDisable()` resets all instance flags, counters, queues, and debounces.
- [ ] No empty `catch {}` blocks present.
- [ ] Reflection lookups cached on hot paths (`MethodInfo` dictionary).
- [ ] No hardcoded user-facing English strings in UI components.
- [ ] Stat displays use `NexusEditorStyles.CreateStatTile(...)`.

### Automated Testing
- [ ] At least 1 NUnit test per P1/P2 fix added under `Tests/Editor/`.
- [ ] Test covers complete lifecycle (`CreateView`, `OnEnable`, `OnUpdate`, `OnDisable`).
- [ ] `InfrastructureValidationTests` passes cleanly.

### Documentation & Versioning
- [ ] `CHANGELOG.md` updated under `[Unreleased]` following Keep a Changelog.
- [ ] `LOCALIZATION_KEYS.md` updated if new i18n keys were added.
- [ ] Public API methods have XML doc comments.
- [ ] Cross-references in related documentation updated.

---

## 📝 Submitting Pull Requests

1. **Format Commit Messages**: Use [Conventional Commits](https://www.conventionalcommits.org/):
   - `fix(tracer): replace ScrollView rebuild with ListView virtualization`
   - `perf(dashboard): cache assembly catalog and debounce search input`
   - `docs(readme): update language policy to en and tr`
2. **Verify NUnit Tests**: Run tests via Unity Test Runner or command line:
   ```bash
   Unity.exe -batchmode -runTests -testPlatform EditMode -projectPath .
   ```

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Framework index, decision flows, and file map
- 📖 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) — Plugin development & edge cases
- 📜 [CHANGELOG.md](../CHANGELOG.md) — Release notes and changelog

---

**Last updated:** 2026-07-24  
**Code version:** 0.4.0  
**Maintainers:** Nexus Core Team  
**Re-review trigger:** Any pull request submission or API change.
