Developer Onboarding — com.nexus.core

Purpose
- Provide quick-start and verification steps for a junior integrator.

How to run tests (EditMode & Runtime)
- Open the Unity project in Unity Editor.
- Window -> General -> Test Runner.
- Run Edit Mode and Runtime tests (EditMode tests run under com.nexus.core.Tests.Editor asmdef; Runtime tests run under com.nexus.core.Tests asmdef).

Key validation checklist (after pulling main)
1. Build the project in the Editor.
2. Run all EditMode tests; address any failing tests.
3. Run all PlayMode tests manually if PlayMode tests are present (audio/haptics/device behaviors require PlayMode/device testing).

Important service contracts
- SignalBus
  - Concurrent commands require ALL handlers for a given signal to support concurrent semantics; dispatcher makes decision accordingly.
  - Use FireAsync for async handlers to get deterministic retry/recovery semantics.

- NexusDI
  - Method injection supports named bindings; ReInject preserves already-resolved parameters.

- SecureObservable
  - Do NOT call SetWithoutNotify unless you know what you're doing; default validates canary and raises tamper.
  - Use ClearOnTamperDetected helper to clear static tamper events where necessary.

- GameSaveManager
  - Saves are atomic via temp-file replace; test harness allows overriding TestSaveDirectory for unit tests.

Performance & testing guidance
- Hot-paths: SignalBus.Fire, TickService.OnTick, AudioService.PlaySfx
  - Avoid allocations in per-frame paths; use pooled arrays and preallocated buffers where possible.
- To stress-test concurrency, run the new runtime stress tests in Tests/Runtime. These tests are EditMode-safe and do not require device-specific hardware.

Troubleshooting and common fixes
- If you see CS0070 complaining about events being assigned to null, ensure the ClearOnTamperDetected() helper is called instead of direct assignment.
- If teardown is non-deterministic (async disposes running after Context.Dispose), wait for up to a short bounded timeout in OnDestroy or call DisposeAsync where possible.

Contact & next steps
- For device-specific validation (Android vibration behavior, iOS haptics), perform manual PlayMode tests on device and report results back.
