# Nexus repository guide

Nexus is an open-source Unity 6 MVCS framework (`com.nexus.core`) meant to be used as the
foundation of several games and by developers of every level. Everything in this repository
must be current, consistent and verifiable. Read this file before changing code or documents.

## Map

| Path | Role |
|---|---|
| `Nexus/Packages/com.nexus.core/` | The product. `Runtime/` (player code), `Editor/` (tools, codegen, validation), `Tests/Editor` + `Tests/Runtime` (Unity NUnit), `Samples~/` (3 importable samples), `SourceGenerator~/` (Roslyn binder generator), `docs/` (user documentation), `CHANGELOG.md` (the only changelog) |
| `Nexus/` | Unity 6000.5.6f1 development project hosting the package. `Assets/Scripts/Game/Samples` holds the wizard's canonical generated files; `Assets/Scripts/Nexus/` holds the git-ignored generated binder |
| `tools/nexus-benchmark/` | .NET host harness; compiles the runtime against stubs (`UnityStubs.cs`, `GeneratorStubs.cs`) and runs every proof suite. Runs as the git pre-commit hook |
| `tools/nexus-comparison/` | Pinned VContainer/Zenject/Reflex DI comparison (host and Unity Mono player) |
| `tools/nexus-consumer/` | Clean UPM consumer gate: packs the tarball, fresh project, sample import, tests, player |
| `tools/unity-verify/` | Player builds of the development project (`NexusVerify.NexusVerifyBuild.*`) |
| `docs/RELEASE_GATES.md` | The gates and the latest measured numbers |
| `docs/research/` | Dated, sourced research (competitors) |
| `docs/archive/` | Past audits, reviews and roadmaps; evidence only, never updated |

## Commands

```powershell
dotnet run --project tools/nexus-benchmark                       # host harness, ~10 s
dotnet run --project tools/nexus-benchmark -- --coverage         # which runtime files it compiles
python tools/check-docs.py                                       # markdown link check
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -projectPath Nexus -runTests -testPlatform EditMode -testResults Nexus\artifacts\editmode.xml -logFile Nexus\artifacts\editmode.log
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -projectPath Nexus -runTests -testPlatform PlayMode -testResults Nexus\artifacts\playmode.xml -logFile Nexus\artifacts\playmode.log
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath Nexus -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder
```

Unity runs one instance per project: never start a Unity command while another is running on
`Nexus/`, and do not edit files under `Nexus/` while a batch run is in progress.
A player build leaves the editor on that build target; `NexusVerifyBuild` switches back, but after any
other Android build run `Unity.exe -batchmode -nographics -quit -projectPath Nexus -buildTarget Win64`
before tests, or the Standalone define `NEXUS_DEBUG` silently drops out of editor compilation.

## Rules

**Code**
- Runtime code must compile in the host harness too. A new public API used by `NexusCodeGenerator.cs` needs its mirror in `tools/nexus-benchmark/GeneratorStubs.cs`.
- Keep the allocation contracts in the package README "Performance scope" table; a change on a hot path needs a harness proof (GCAudit or a suite assertion).
- Files use CRLF line endings on disk (`core.autocrlf=true`); new `.cs` and `.md` files follow that.
- New source files under the package need their Unity `.meta` file: run any Unity command once and commit the generated `.meta` with the file.

**Tests**
- A test locks a behaviour that was wrong or can regress, with a name that states the behaviour. No tests that restate the implementation, assert constants, duplicate another test, or exist to raise a count.
- Package EditMode tests live in `Tests/Editor` under namespace `Nexus.Editor.Tests`; runtime/PlayMode tests in `Tests/Runtime`.
- Report test results as numbers from the result file (passed / failed / skipped), never "all green" without them.

**Documentation**
- One canonical document per topic. User documentation lives in the package `docs/`; repository-level status lives in `docs/`; dated reports go to `docs/archive/` with a row in its README and are never edited afterwards.
- English is canonical; `*_TR.md` files are translations and change in the same commit as their source.
- Every number (test counts, file counts, versions, benchmark values) and every path or menu name in a document is checked against the code when the document is touched. No "last updated" footers; the git history is the date.
- The package `CHANGELOG.md` is the only changelog. `package.json` is the released version; everything newer is under `[Unreleased]`.
- No performance claim without workload, platform, Unity version, competitor versions and reproduction tool. "Verified" means a result file exists.

**Consumers**
- Games consume the package by git URL pinned to a commit. Framework fixes are made here with a regression test and the game re-pins; a game never keeps an edited copy of the package. Known consumers: Gigawatt, Pixel-Flow-Clone, IdleProject, PuzzleGame, RingFlow (sibling folders under `..\`).

**Commits**
- Conventional prefixes used here: `fix(di):`, `fix(codegen):`, `fix(lifecycle):`, `feat:`, `docs:`, `test:`, `chore:`. One logical change per commit; the message states what was wrong and how it was verified.
- The pre-commit hook runs the host harness; a commit that fails it is not committed.
