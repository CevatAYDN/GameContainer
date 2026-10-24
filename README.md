# Nexus

Nexus is an open-source MVCS (Model, View, Command, Service) architecture framework for Unity 6,
shipped as the UPM package `com.nexus.core`. It combines a dependency-injection container, a
signal bus with pooled commands, reactive models, a view/mediator layer, a service suite for
mobile and PC games, and an editor tool suite in one package.

| | |
|---|---|
| Package | `Nexus/Packages/com.nexus.core` (version in `package.json`: **0.4.0**; unreleased work is listed in its `CHANGELOG.md`) |
| Unity | 6000.0 minimum (`package.json`); verified on **6000.5.6f1** |
| License | MIT |
| Verification status | [docs/RELEASE_GATES.md](docs/RELEASE_GATES.md) |

[Türkçe özet aşağıda.](#türkçe)

## Install

Window → Package Manager → **+** → *Install package from git URL*:

```text
https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.core#<commit>
```

Pin a commit (`#<commit>`) or a release tag in production projects. The repository root is a
monorepo named `GameContainer`; the package name stays `com.nexus.core`.

Rules for games that consume Nexus:

- Framework fixes are made in this repository, with a regression test, and then the game
  re-pins to the new commit. A game never keeps an edited copy of the package.
- Several games may consume the same commit; nothing in the package depends on a specific game.

## Start here

| Level | You | Read, in this order |
|---|---|---|
| 0 | Never used dependency injection or a signal bus | [10_MIN_QUICKSTART.md](Nexus/Packages/com.nexus.core/docs/10_MIN_QUICKSTART.md) ([TR](Nexus/Packages/com.nexus.core/docs/10_MIN_QUICKSTART_TR.md)), then the **Counter Example** sample |
| 1 | Shipping a feature in a new Nexus project | [walkthrough.md](Nexus/Packages/com.nexus.core/walkthrough.md), [CANONICAL-PATTERNS.md](Nexus/Packages/com.nexus.core/docs/CANONICAL-PATTERNS.md) |
| 2 | Adding Nexus to an existing game | [GETTING_STARTED.md](Nexus/Packages/com.nexus.core/docs/GETTING_STARTED.md) ([TR](Nexus/Packages/com.nexus.core/docs/GETTING_STARTED_TR.md)), [INTEGRATION_PATHS.md](Nexus/Packages/com.nexus.core/docs/INTEGRATION_PATHS.md) ([TR](Nexus/Packages/com.nexus.core/docs/INTEGRATION_PATHS_TR.md)); from Zenject: [ZENJECT_MIGRATION.md](Nexus/Packages/com.nexus.core/docs/ZENJECT_MIGRATION.md) |
| 3 | Studio: release builds, performance, tooling | [ARCHITECTURE.md](Nexus/Packages/com.nexus.core/docs/ARCHITECTURE.md), [HARDENING.md](Nexus/Packages/com.nexus.core/docs/HARDENING.md), [PERFORMANCE_COMPARISON.md](Nexus/Packages/com.nexus.core/docs/PERFORMANCE_COMPARISON.md), [ADAPTERS.md](Nexus/Packages/com.nexus.core/docs/ADAPTERS.md), [PLUGIN_DEVELOPMENT.md](Nexus/Packages/com.nexus.core/docs/PLUGIN_DEVELOPMENT.md), the Dashboard's build-readiness card |
| AI agent | Generating game code on Nexus | [AI_GAME_DEVELOPER_GUIDE.md](Nexus/Packages/com.nexus.core/docs/AI_GAME_DEVELOPER_GUIDE.md), [GAME_PATTERNS.md](Nexus/Packages/com.nexus.core/docs/GAME_PATTERNS.md) |

Turkish readers: [THE_BIG_NEXUS_HOWTO.md](Nexus/Packages/com.nexus.core/docs/THE_BIG_NEXUS_HOWTO.md) tells the same story as
the walkthrough as a narrative; the `*_TR.md` files are translations of their English sources.

Everything else: the [package README](Nexus/Packages/com.nexus.core/README.md).

Three importable samples ship with the package: **Code-first Startup** (one component),
**Counter Example** (full MVCS tour) and **Nexus Starter Package** (scene scaffold).

## Repository layout

| Path | Contents |
|---|---|
| `Nexus/Packages/com.nexus.core` | The package: `Runtime`, `Editor`, `Tests`, `Samples~`, `SourceGenerator~`, `docs`, `CHANGELOG.md` |
| `Nexus/` | Unity 6000.5.6f1 development project that hosts the package and its EditMode/PlayMode tests |
| `tools/nexus-benchmark` | .NET host harness: compiles the runtime against Unity stubs and runs stress, fuzz, zero-allocation, lifecycle, codegen and evidence suites; also the pre-commit hook |
| `tools/nexus-comparison` | Pinned VContainer/Zenject/Reflex core DI comparison (host and Unity Mono player) |
| `tools/nexus-consumer` | Clean UPM consumer gate: pack, fresh project, sample import, tests, player |
| `tools/unity-verify`, `tools/nexus-device-test` | Player build verification of the development project and device test helpers |
| `tools/check-docs.py` | Markdown link checker used by the documentation gate |
| `docs/` | Repository-level documents: release gates, SLOs, ADRs, dated research, archive of past audits |

## Verify a change

```powershell
# Host harness (also runs on every commit through the pre-commit hook)
dotnet run --project tools/nexus-benchmark

# Unity tests, headless (Unity 6000.5.6f1)
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -projectPath Nexus -runTests -testPlatform EditMode -testResults artifacts\editmode.xml -logFile artifacts\editmode.log
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -projectPath Nexus -runTests -testPlatform PlayMode -testResults artifacts\playmode.xml -logFile artifacts\playmode.log

# Regenerate the AOT binder of the development project
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath Nexus -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder

# Documentation links
python tools/check-docs.py
```

The gates a release must pass, with the latest measured numbers, are in
[docs/RELEASE_GATES.md](docs/RELEASE_GATES.md). Documentation rules and the project map for
contributors and AI agents are in [CLAUDE.md](CLAUDE.md). A problem or a request starts as an
issue in this repository; nobody changes the repository without one.

## Türkçe

Nexus, Unity 6 için açık kaynak bir MVCS mimari çatısıdır ve `com.nexus.core` UPM paketi
olarak dağıtılır. Tek pakette bağımlılık enjeksiyonu, havuzlu komutlarla sinyal veriyolu,
reaktif modeller, view/mediator katmanı, mobil ve PC oyunları için servis takımı ve editör
araçları bulunur. Kurulum için yukarıdaki git URL'sini Package Manager'a ekleyin ve üretim
projelerinde bir commit'e sabitleyin. Türkçe rehberler:
[10 dakikada hızlı başlangıç](Nexus/Packages/com.nexus.core/docs/10_MIN_QUICKSTART_TR.md),
[mevcut projeye ekleme](Nexus/Packages/com.nexus.core/docs/GETTING_STARTED_TR.md),
[entegrasyon yolları](Nexus/Packages/com.nexus.core/docs/INTEGRATION_PATHS_TR.md).
Çatı düzeltmeleri yalnızca bu depoda yapılır; oyunlar paketin düzenlenmiş bir kopyasını
taşımaz, yeni commit'e sabitlenir. Sorun ya da istek önce bu depoda issue olarak açılır; kimse depoyu
kendi başına değiştirmez.

## License

MIT. See [LICENSE](LICENSE).
