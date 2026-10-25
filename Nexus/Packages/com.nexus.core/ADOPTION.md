# Adopting Nexus in a Studio (Bus-Factor / Single-Developer Risk)

> **Gap P2-D.** Nexus is authored and maintained primarily by a single developer.
> For multi-game studio use, **fork and own the package** rather than consuming the
> upstream `GameContainer` repo (`https://github.com/CevatAYDN/GameContainer`) directly.

## Why fork

- Upstream is a single-maintainer project. Relying on it directly couples your
  release cadence to someone else's availability.
- You will eventually need fixes, service additions, or validation-rule tweaks
  that are specific to your games. Owning the fork lets you ship them immediately.
- A fork also lets you pin an exact, known-good version per game.

## Recommended setup

1. Fork `GameContainer` into your studio org (e.g. `your-org/GameContainer`). The package lives in
   that repository at `Nexus/Packages/com.nexus.core`.
2. Pin a version tag such as `v0.5.0`, or a full 40-character commit hash (see `docs/STABILITY.md`
   for what stays stable before 1.0).
3. Each game consumes the fork via a UPM git dependency:

```json
// Packages/manifest.json
{
  "dependencies": {
    "com.nexus.core": "https://github.com/<your-org>/GameContainer.git?path=/Nexus/Packages/com.nexus.core#<tag>"
  }
}
```

Use only the pinned git URL: no git submodule, no `file:` path and no embedded copy of the package
in a game. One pinned copy of the framework per game means a fix is made once in the fork and
each game re-pins.

## What to own vs. what not to fork

**Own (customize in your fork):**
- Service implementations (`Runtime/Services/...`) — adapt to your backend, analytics, ads, IAP.
- Validation rules (`Editor/Validation/BuildValidation.cs`) — enforce your team's conventions.
- Binder output path / AOT settings (`NexusEditorSettings`).

**Do NOT fork just to:**
- Bump the version or rename the package — use tags/releases instead.
- Add game-specific commands — those belong in the *game* project, not the package.

## Real-game reference

For this repository's own games (Gigawatt, Wickling, ChipCrisis and others) the target is the same: a
pinned git URL for every game. The infrastructure lives in the package, the game logic lives in each
game's repository, and a framework fix is made in the package and re-pinned, never edited in a game's
copy.
