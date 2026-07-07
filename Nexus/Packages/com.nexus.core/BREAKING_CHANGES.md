# Breaking Change Policy

This document outlines Nexus Core's policy regarding breaking changes, semantic versioning, and API stability.

## Table of Contents

- [Semantic Versioning](#semantic-versioning)
- [What Constitutes a Breaking Change](#what-constitutes-a-breaking-change)
- [Change Categories](#change-categories)
- [Deprecation Process](#deprecation-process)
- [Migration Support](#migration-support)
- [Version Lifecycle](#version-lifecycle)
- [Exceptions](#exceptions)

---

## Semantic Versioning

Nexus Core follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html):

### Version Format

```
MAJOR.MINOR.PATCH
```

- **MAJOR**: Incompatible API changes
- **MINOR**: Backwards-compatible functionality additions
- **PATCH**: Backwards-compatible bug fixes

### Examples

- `0.3.0` → `0.4.0`: New features, backwards compatible
- `0.3.0` → `1.0.0`: Breaking changes, major release
- `0.3.0` → `0.3.1`: Bug fixes, backwards compatible

### Pre-Release Versions

Until version `1.0.0`, Nexus Core is in pre-release:
- Minor versions may contain breaking changes
- Changelog will document all breaking changes
- Migration guides provided for each version

---

## What Constitutes a Breaking Change

A change is considered breaking if it requires users to modify their code to continue working.

### Clear Breaking Changes

1. **Removed or Renamed Types**
   - Classes, interfaces, structs removed
   - Type names changed
   - Namespaces reorganized

2. **Removed or Renamed Members**
   - Public methods/properties/fields removed
   - Member signatures changed
   - Member access modifiers changed (public → private)

3. **Interface Changes**
   - Methods added to interfaces (requires implementation)
   - Interface inheritance changed
   - Generic constraints modified

4. **Behavior Changes**
   - Method behavior fundamentally changed
   - Default parameter values changed
   - Exception types changed
   - Throwing behavior changed

5. **Attribute Changes**
   - Required attributes added
   - Attribute behavior changed
   - Attribute parameters changed

### Potential Breaking Changes

These changes may break code but are sometimes necessary:

1. **Enum Value Changes**
   - Adding enum values (can break switch statements)
   - Renaming enum values

2. **Generic Constraints**
   - Adding generic constraints
   - Tightening constraints

3. **Event Signatures**
   - Adding event parameters
   - Changing event handler types

### Non-Breaking Changes

These changes are safe and don't require code modification:

1. **New Types and Members**
   - New classes, interfaces, structs
   - New public methods/properties/fields
   - New overloads

2. **Behavior Improvements**
   - Performance optimizations
   - Bug fixes that don't change API
   - Better error messages

3. **Internal Changes**
   - Private implementation details
   - Internal refactoring
   - Code generation improvements

---

## Change Categories

### Category 1: Major Breaking Changes

**Requires:** Major version bump (MAJOR)

**Examples:**
- Removing core interfaces (ICommand, ISignalBus)
- Renaming fundamental types (NexusDI → Container)
- Changing architectural patterns (MVCS → MVVM)
- Removing entire subsystems (Service layer)

**Process:**
1. Announce 3+ months in advance
2. Provide detailed migration guide
3. Maintain old version for 6+ months
4. Offer support during transition

### Category 2: Moderate Breaking Changes

**Requires:** Minor version bump in pre-release, Major in post-1.0

**Examples:**
- Adding methods to interfaces
- Changing method signatures
- Removing non-essential types
- Renaming members

**Process:**
1. Announce 1+ month in advance
2. Provide migration guide
3. Maintain old version for 3+ months
4. Automated migration tools if possible

### Category 3: Minor Breaking Changes

**Requires:** Minor version bump (pre-release only)

**Examples:**
- Adding enum values
- Tightening generic constraints
- Changing default parameter values
- Adding required attributes

**Process:**
1. Document in changelog
2. Provide code examples
3. Quick migration steps

---

## Deprecation Process

### Deprecation Lifecycle

1. **Announcement**
   - Mark as `[Obsolete]` with message
   - Document in changelog
   - Provide migration path

2. **Warning Period**
   - Maintain for at least 2 minor versions
   - Compiler warnings during this period
   - Documentation updated

3. **Removal**
   - Remove after warning period
   - Update migration guide
   - Release notes

### Obsolete Attribute Usage

```csharp
// Simple deprecation
[Obsolete("Use NewMethod instead")]
public void OldMethod() { }

// Deprecation with error
[Obsolete("Use NewMethod instead", error: true)]
public void VeryOldMethod() { }

// Deprecation with migration path
[Obsolete("Use NewMethod instead. See MIGRATION.md section 3.2")]
public void OldMethodWithDocs() { }
```

### Deprecation Timeline

- **0.x versions**: 1 minor version warning period
- **1.x+ versions**: 2 minor versions warning period
- **Critical APIs**: 3+ minor versions warning period

---

## Migration Support

### Migration Guides

Each breaking change includes:

1. **Before/After Code Examples**
   ```csharp
   // Before
   public void OnBind(IContext context) { }
   
   // After
   public ValueTask OnBind(CancellationToken ct) => default;
   ```

2. **Automated Migration**
   - Roslyn analyzers where possible
   - Code generation scripts
   - Find-and-replace patterns

3. **Step-by-Step Instructions**
   - Clear migration steps
   - Common pitfalls
   - Verification steps

### Backwards Compatibility

Where possible, maintain compatibility:

1. **Graceful Degradation**
   ```csharp
   public void Execute(Signal signal)
   {
       // Old behavior maintained for legacy code
       // New behavior for updated code
   }
   ```

2. **Adapter Pattern**
   ```csharp
   // Provide adapter for old API
   public class LegacyCommandAdapter : ICommand<OldSignal>
   {
       public void Execute(OldSignal signal)
       {
           // Convert to new signal
           var newSignal = Convert(signal);
           _newCommand.Execute(newSignal);
       }
   }
   ```

3. **Configuration Flags**
   ```csharp
   // Allow opting into new behavior
   public bool EnableNewBehavior { get; set; }
   ```

---

## Version Lifecycle

### Pre-Release (0.x)

- **Stability**: Experimental
- **Breaking Changes**: Allowed in minor versions
- **Support**: Best effort
- **Duration**: Until 1.0.0 release

### Stable Release (1.x+)

- **Stability**: Production-ready
- **Breaking Changes**: Major versions only
- **Support**: 12 months from release
- **Security Updates**: Critical fixes only

### Long-Term Support (LTS)

- **Stability**: Enterprise-ready
- **Breaking Changes**: Rare, well-justified
- **Support**: 24+ months
- **Criteria**: Widely adopted, critical infrastructure

### End of Life

When a version reaches EOL:

1. **Announcement** 6 months in advance
2. **Security Updates** Stop (except critical)
3. **Support** Best effort only
4. **Archival** Documentation remains available

---

## Exceptions

### Security Updates

Critical security fixes may:
- Bypass normal deprecation timeline
- Require immediate upgrade
- Include breaking changes if necessary

### Critical Bugs

Bugs causing:
- Data loss
- Security vulnerabilities
- Platform blocking issues

May warrant:
- Immediate patch release
- Breaking changes if unavoidable
- Urgent migration

### Platform Requirements

Changes required by:
- Unity engine updates
- Platform requirements (iOS, Android, etc.)
- C# language changes

May justify:
- Breaking changes
- Shortened deprecation period
- Forced migration

### Beta Features

Features marked as beta:
- May change without notice
- Not covered by stability guarantees
- Opt-in only
- Clear beta documentation

---

## Communication

### Announcement Channels

1. **Changelog** - All changes documented
2. **GitHub Releases** - Major announcements
3. **GitHub Discussions** - Community feedback
4. **Blog Posts** - Major architectural changes

### Timeline Communication

- **6 months**: Major breaking changes announced
- **3 months**: Detailed migration guides available
- **1 month**: Final reminder before release
- **Release**: Complete documentation

### Feedback Process

1. **RFC (Request for Comments)** for major changes
2. **Community discussion** period
3. **Feedback incorporation**
4. **Final decision** with rationale

---

## Best Practices

### For Users

1. **Stay Updated**
   - Read changelogs
   - Follow deprecation warnings
   - Test beta versions

2. **Plan Upgrades**
   - Schedule upgrade time
   - Test in staging environment
   - Have rollback plan

3. **Provide Feedback**
   - Report issues early
   - Suggest improvements
   - Share migration experiences

### For Contributors

1. **Consider Stability**
   - Avoid breaking changes
   - Use deprecation process
   - Provide migration paths

2. **Document Changes**
   - Update changelog
   - Add migration guide
   - Include examples

3. **Test Compatibility**
   - Test against previous versions
   - Verify migration steps
   - Check edge cases

---

## Version History

### Recent Breaking Changes

#### Version 0.3.0 (2026-07-07)

**Breaking Changes:**
- IReactiveModel.OnBind signature changed
- Service interfaces updated (IPlayerPrefsService, IAudioService, IHapticService)
- MockContext moved to centralized location

**Migration:** See [MIGRATION.md](MIGRATION.md#version-020--030)

#### Version 0.2.0 (2026-06-28)

**Breaking Changes:**
- Signal handler protection (Fire() with async handlers)
- Command interface mutual exclusivity
- FireAsyncAndForget return type

**Migration:** See [MIGRATION.md](MIGRATION.md#version-010--020)

---

## Questions?

If you have questions about this policy:

1. **Read this document** thoroughly
2. **Check MIGRATION.md** for specific version changes
3. **Open a GitHub Discussion** for clarification
4. **Review GitHub Issues** for similar questions

---

**Last Updated:** 2026-07-07
**Nexus Core Version:** 0.3.0
**Policy Version:** 1.0
