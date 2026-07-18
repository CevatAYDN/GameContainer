# Adopting Nexus in a Studio (Bus-Factor / Single-Developer Risk)

> **Gap P2-D.** Nexus is authored and maintained primarily by a single developer.
> For multi-game studio use, **fork and own the package** rather than consuming the
> upstream `GameContainer` repo (`github.com/CevatAYDN/GameContainer`) directly.

## Why fork

- Upstream is a single-maintainer project. Relying on it directly couples your
  release cadence to someone else's availability.
- You will eventually need fixes, service additions, or validation-rule tweaks
  that are specific to your games. Owning the fork lets you ship them immediately.
- A fork also lets you pin an exact, known-good version per game.

## Recommended setup

1. Fork `com.nexus.core` into your studio org (e.g. `your-org/com.nexus.core`).
2. Pin a version tag (see `STABILITY.md` for the 1.0 freeze checklist).
3. Each game consumes the fork via a UPM git dependency:

```json
// Packages/manifest.json
{
  "dependencies": {
    "com.nexus.core": "https://github.com/<your-org>/com.nexus.core.git?path=Packages/com.nexus.core#<tag>"
  }
}
```

Git submodules work too:

```bash
git submodule add https://github.com/<your-org>/com.nexus.core.git Packages/com.nexus.core
```

## What to own vs. what not to fork

**Own (customize in your fork):**
- Service implementations (`Runtime/Services/...`) — adapt to your backend, analytics, ads, IAP.
- Validation rules (`Editor/Validation/BuildValidation.cs`) — enforce your team's conventions.
- Binder output path / AOT settings (`NexusEditorSettings`).

**Do NOT fork just to:**
- Bump the version or rename the package — use tags/releases instead.
- Add game-specific commands — those belong in the *game* project, not the package.

## Real-game reference

The reference real-game consumer is **RingFlow** (`https://github.com/CevatAYDN/RingFlow`),
which uses Nexus the same way your games should: the infrastructure lives in the
fork, the game logic lives in the game repo.
