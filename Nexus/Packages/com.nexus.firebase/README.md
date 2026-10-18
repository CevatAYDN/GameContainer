# Nexus Firebase (`com.nexus.firebase`)

> **Who this is for:** level 3, shipping a game that uses Firebase Analytics, Remote Config or Crashlytics with Nexus.

The rules and mocks that the Firebase services follow, as Nexus contracts that compile and are tested without any Firebase SDK installed.

## What version 0.1.0 contains

| Type | Role |
|---|---|
| `AnalyticsEventName` | The names Firebase accepts: snake_case, starting with a letter, at most 40 characters. A name it refuses is dropped by the SDK without a word, so it is checked first. |
| `ConsentState`, `AnalyticsCollectionPolicy` | The consent answer, and whether analytics and crash collection may run. Off while the consent is unknown or required (the EEA and the UK), on once it is obtained or was never required. |
| `IRemoteConfigService` | `FetchAsync`, `GetBool` / `GetInt` / `GetDouble` / `GetString` with the game's fallback, and a `Changed` event per key. |
| `RemoteConfigFetchPolicy` | A fetch is due on the first launch, once the cache is 12 hours old, and when the clock reads earlier than the last fetch. |
| `RemoteConfigParser` | Reads a raw value as a typed value or returns the fallback. Numbers use the invariant culture. |
| `MockAnalyticsService`, `MockRemoteConfigService` | In-memory services for the editor, development builds and tests. The analytics mock drops the names the Firebase service would drop. |

Not in 0.1.0: the Firebase SDK adapters (`FirebaseAnalyticsService`, `FirebaseRemoteConfigService`) and the Crashlytics reporter. They are added once the SDK is installed, and their assembly compiles only while it is (`versionDefines`), like `com.nexus.ads.admob`.

## Rules

- **Event and parameter names.** `AnalyticsEventName.IsValid` (`Tests/Editor/AnalyticsRulesTests.cs`).
- **Consent.** Analytics stay off until the consent update has answered. A cached answer from an earlier session is readable at launch, so a player who already answered gets analytics from launch. The consent form itself is shown by the ads package (`ConsentForm.LoadAndShowConsentFormIfRequired` in the Google Mobile Ads plugin), not by this package.
- **Remote config.** Fetched at launch and reused for 12 hours. A value that cannot be read as the requested type returns the fallback. A new value applies at the next run start in the game, never inside a run.

## Tests

`Tests/Editor` (namespace `Nexus.Firebase.Tests`) runs in the EditMode of the Nexus development project. Results are read from the result file, not from "green".
