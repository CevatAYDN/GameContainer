# Nexus Firebase (`com.nexus.firebase`)

> **Who this is for:** level 3, shipping a game with Firebase Analytics, Remote Config or Crashlytics on Nexus.

The rules the Firebase services follow, and their mocks, as Nexus contracts that compile and are tested without any Firebase SDK installed. The SDK adapters are not in 0.1.0; they are added once the SDK is installed, and their assembly then compiles only while it is (`versionDefines`), like `com.nexus.ads.admob`.

## What 0.1.0 contains

| Type | Role |
|---|---|
| `AnalyticsEventName`, `AnalyticsUserProperty` | The name and value limits of events, parameters and user properties. |
| `ConsentState`, `AnalyticsChoice`, `AnalyticsDecision`, `AnalyticsCollectionPolicy` | The consent answer, the player's choice, and whether an event is sent, held or dropped. |
| `AnalyticsGate`, `AnalyticsEvent` | Holds events until the answer is known, sends them in order once allowed, drops them when refused. The wait is bounded (256 events). |
| `IRemoteConfigService` | `FetchAsync`, typed reads with the game's fallback, and a `Changed` event per key. |
| `RemoteConfigFetchPolicy`, `RemoteConfigParser` | A 12-hour fetch cache, and value parsing with the invariant culture. |
| `MockAnalyticsService`, `MockRemoteConfigService` | `NexusService` subclasses for the editor, development builds and tests. The analytics mock drops what the Firebase service would drop. |

## Rules

- **Event and parameter names.** Letters, digits and underscores, starting with a letter, at most 40 characters, and no reserved prefix (`firebase_`, `google_`, `ga_`). Firebase compares names case-sensitively, so case is kept. The 40-character and letter-first limits are stated in Google's documentation for parameter names; they are applied to event names too, and the event-name page should be checked before 1.0.
- **User properties.** A name of at most 24 characters with the same shape, not `user_id`, `first_open_time` or `last_deep_link_referrer`; a value of at most 36 characters. A value over the limit is dropped, not truncated.
- **Consent.** The consent status does not say which purposes the player accepted: `Obtained` means the form was answered. Analytics are sent when the choice is granted, dropped when it is refused, and held while the choice or the answer is unknown. Where consent is not required, events are sent at once. The place the choice is read from is the adapter's job and is not in 0.1.0.
- **Remote config.** Fetched at launch and reused for 12 hours. A clock set back forces a fetch. A value that cannot be read as the requested type returns the fallback. Numbers are read with the invariant culture, so a device language does not change them.

## Not checked by 0.1.0

- The string length of event parameter values (100 characters for standard properties, per Google's documentation).
- The number of parameters per event and the 500 event types per app.
- Where the adapter reads the player's analytics choice from (open until the adapter is written).

## Tests

`Tests/Editor` (namespace `Nexus.Firebase.Tests`) runs in the EditMode of the Nexus development project. Results are read from the result file.