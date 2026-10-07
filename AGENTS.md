# Working on this repo (agent notes)

## What this repository is

`Nexus` (`com.nexus.core`) is a **Unity 6 / C# framework** — a UPM package with an
MVCS/DI architecture. The root also holds a Unity project (`Nexus/`) and .NET tooling
(`tools/`). It is **not a web application**.

The Unity editor (6000.5.6f1, per `Nexus/ProjectSettings/ProjectVersion.txt`) is not
available in the sandbox — it is a multi-GB licensed Windows/Linux editor, and the
`Nexus/` project has no `Library/` folder. So the runnable, verifiable artifact here is
the **standalone .NET 10 benchmark harness** in `tools/nexus-benchmark/`, which is also
the project's own CI path (`.github/workflows/nexus-ci.yml`, job `harness` on
`ubuntu-latest`). It compiles the *real* runtime sources from
`Nexus/Packages/com.nexus.core/Runtime/**` and asserts the full contract: 0-GC hot
paths, DI/lifetime scopes, contexts, services, encrypted storage, codegen, stress and
fuzz suites.

## Running it (Base44 dev environment)

`docker-compose.base44.yml` defines two services:

- `harness` — one-shot .NET 10 SDK job (`tools/nexus-benchmark/preview/run-harness.sh`):
  builds and runs `dotnet run -c Release --no-build -- --json`, writes
  `raw.log`, `build.log`, `exitcode`, `generated-at.txt` into the `nexus-report` volume.
  It deliberately **always exits 0** once the report is written, so the viewer comes up
  even when the harness finds regressions (the report itself carries pass/fail).
- `preview` — `nginx:alpine` on host port 3000, serving that volume as its web root,
  with `preview/index.html` bind-mounted over it as the report viewer.

```bash
docker compose -f docker-compose.base44.yml up -d      # first boot / full re-run
docker compose -f docker-compose.base44.yml run --rm harness   # re-run the pipeline only
docker compose -f docker-compose.base44.yml logs -f preview
```

After changing runtime code under `Nexus/Packages/com.nexus.core/`, re-run the
`harness` service and reload the page — the harness reads sources from the bind mount,
so no image rebuild is needed. `preview/index.html` is bind-mounted, so edits to it show
up on a plain page reload.

The viewer is a plain HTML/JS page: it fetches `raw.log`, splits off the trailing
`--json` document (which starts at the first line that is exactly `{`), and renders the
suite/test results. It is dev tooling only — no application behavior depends on it.

## Gotchas

- **`tools/nexus-benchmark/NexusBenchmark.csproj` uses a hand-maintained `<Compile>` list**,
  not a glob over `Runtime/`. Adding a runtime file that an already-listed file calls
  breaks the harness build with `CS0103` until it is added. This already bit once:
  `NexusDI.ValidateBindings` delegates to `DiBindingValidator`, and
  `Runtime/Core/DiBindingValidator.cs` was missing from the list (fixed). Unity itself is
  unaffected — it compiles everything under `Runtime/`.
- **`tools/nexus-compile-check/`** references Unity's own assemblies
  (`Nexus/Library/ScriptAssemblies/UnityEngine.*.dll`, `UNITY_MODULES`), which do not
  exist in the sandbox. It cannot build here; it is a Unity-machine tool.
- **`tools/unity-verify/verify-unity-build.sh`** requires a licensed Unity 6000.5.6f1
  editor with Windows build support. Not runnable in the sandbox.
- **No external credentials are involved.** The harness has no NuGet dependencies
  (Roslyn comes from the SDK's `Roslyn/bincore`) and its ads/IAP/analytics providers are
  in-repo mocks. `.base44/environment.json` therefore declares no secrets.
- **No sandbox-only code overrides exist.** Nothing in the app reads
  `BASE44_PREVIEW_MODE`; nginx serves plain static files with no host/origin allowlist
  to extend, so no config is gated on that flag.

## How to verify the app is working

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:3000/   # 200
curl -s http://localhost:3000/exitcode                            # 0
docker compose -f docker-compose.base44.yml ps                    # preview: healthy
```

A green run means the harness printed `ALL BENCHMARKS PASSED ✓` and the JSON totals show
`failed: 0`. Current full pipeline: ~285 tests across 19 suites, a few seconds on 4 cores.
