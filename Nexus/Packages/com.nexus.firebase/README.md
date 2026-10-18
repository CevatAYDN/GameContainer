# Nexus Firebase (`com.nexus.firebase`)

> **Who this is for:** level 3, shipping a game with Firebase Analytics, Remote Config or Crashlytics on Nexus.

Firebase Analytics, Remote Config and Crashlytics behind Nexus services, with the rules they follow and mocks for the editor, development builds and headless tests. The rules and the mocks compile with no Firebase SDK installed; the three SDK adapters compile only while their SDK package is (`versionDefines`), like `com.nexus.ads.admob`.

## What 0.1.0 contains

| Type | Role |
|---|---|
| `AnalyticsEventName`, `AnalyticsUserProperty`, `AnalyticsParameterValue` | The name and value limits of events, parameters and user properties, and which Firebase parameter type a value becomes (text, 64-bit integer, double; a boolean is 1 or 0; anything else is not sent). |
| `ConsentState`, `AnalyticsChoice`, `AnalyticsDecision`, `AnalyticsCollectionPolicy` | The consent answer, the player's choice, and whether an event is sent, held or dropped. |
| `IAnalyticsConsent` | Where the services read the answer and the choice from. The game implements it over its consent flow. |
| `AnalyticsGate`, `AnalyticsEvent` | Holds events until the answer is known, sends them in order once allowed, drops them when refused. The wait is bounded (256 events). |
| `IRemoteConfigService`, `RemoteConfigFetchPolicy`, `RemoteConfigParser` | `FetchAsync`, typed reads with the game's fallback, a `Changed` event per key, a 12-hour fetch cache, and value parsing with the invariant culture. |
| `ICrashReporter`, `LoggedErrorException` | Records an error the game survived as a non-fatal. |
| `MockAnalyticsService`, `MockRemoteConfigService`, `MockCrashReporter`, `MockAnalyticsConsent` | In-memory versions for the editor, development builds and tests. The analytics mock drops what the Firebase service would drop. |
| `FirebaseAnalyticsService` (`NEXUS_FIREBASE_ANALYTICS`) | Analytics behind `IAnalyticsService`. Collection stays off until the decision allows it. |
| `FirebaseRemoteConfigService` (`NEXUS_FIREBASE_REMOTE_CONFIG`) | Remote Config behind `IRemoteConfigService`. A value the console does not set returns the game's fallback. |
| `FirebaseCrashReporter` (`NEXUS_FIREBASE_CRASHLYTICS`) | Crashlytics behind `ICrashReporter`: reports the commands Nexus's recovery engine gave up on, and logged errors (`Debug.LogError`), as non-fatals. |

## Install the SDK

The adapters were written against Firebase Unity SDK **14.0.0** (`expression` 14.0.0 in the assembly definition) and compiled against it in a disposable project. Google distributes the UPM packages as `.tgz` files, not through a registry: download `com.google.firebase.app`, `.analytics`, `.remote-config` and `.crashlytics` for one version from <https://developers.google.com/unity/archive>, put them in a `GooglePackages` folder next to `Packages`, and add them to `Packages/manifest.json` as `"com.google.firebase.analytics": "file:../GooglePackages/com.google.firebase.analytics-14.0.0.tgz"` and so on. `com.google.firebase.app` depends on the External Dependency Manager (1.2.190), from the OpenUPM registry as for `com.nexus.ads.admob`. All Firebase packages in a project must be the same version.

## Rules

- **Event and parameter names.** Letters, digits and underscores, starting with a letter, at most 40 characters, and no reserved prefix (`firebase_`, `google_`, `ga_`). Firebase compares names case-sensitively, so case is kept. The 40-character and letter-first limits are stated in Google's documentation for parameter names; they are applied to event names too, and the event-name page should be checked before 1.0.
- **User properties.** A name of at most 24 characters with the same shape, not `user_id`, `first_open_time` or `last_deep_link_referrer`; a value of at most 36 characters. A value over the limit is dropped, not truncated.
- **Consent.** The consent status does not say which purposes the player accepted: `Obtained` means the form was answered. Analytics are sent when the choice is granted, dropped when it is refused, and held while the choice or the answer is unknown. Where consent is not required, events are sent at once. Where the choice is read from is the game's job, through `IAnalyticsConsent`.
- **Remote config.** Fetched at launch and reused for 12 hours. A clock set back forces a fetch. A value that cannot be read as the requested type returns the fallback. Numbers are read with the invariant culture, so a device language does not change them.

## Consent and the platform manifest

`SetAnalyticsCollectionEnabled(false)` at start is too late to stop what the SDK collects before the game's code runs. For a game that waits for consent, set `firebase_analytics_collection_enabled` to `false` in the application tag of the Android manifest (Firebase's documented way to delay collection until consent) and let `FirebaseAnalyticsService` turn it on. Crashlytics needs the same at the platform level; the exact flag is not verified here, so check Crashlytics's own documentation.

## Not checked by 0.1.0

- The adapters compile against 14.0.0; they have not been run in the editor or on a device, which needs a `google-services.json`.
- The string length of event parameter values (100 characters for standard properties, per Google's documentation).
- The number of parameters per event and the 500 event types per app.

## Tests

`Tests/Editor` (namespace `Nexus.Firebase.Tests`) runs in the EditMode of the Nexus development project, with no SDK installed. Results are read from the result file.