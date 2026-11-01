# Nexus Firebase (`com.nexus.firebase`)

> **Who this is for:** level 3, shipping a game with Firebase Analytics, Remote Config or Crashlytics on Nexus.

Firebase Analytics, Remote Config and Crashlytics behind Nexus services, with the rules they follow and mocks for the editor and headless tests; a player never binds a mock (issue #171). The rules and the mocks compile with no Firebase SDK installed; the three SDK adapters compile only while their SDK package is (`versionDefines`), like `com.nexus.ads.admob`.

## What 0.1.0 contains

| Type | Role |
|---|---|
| `AnalyticsEventName`, `AnalyticsUserProperty`, `AnalyticsParameterValue` | The name and value limits of events, parameters and user properties, and which Firebase parameter type a value becomes (text, 64-bit integer, double; a boolean is 1 or 0; anything else is not sent). |
| `ConsentState`, `AnalyticsChoice`, `AnalyticsDecision`, `AnalyticsCollectionPolicy` | The consent answer, the player's choice, and whether an event is sent, held or dropped. |
| `IAnalyticsConsent` | Where the services read the answer and the choice from. Over Google's consent form, `com.nexus.ads.admob`'s `UmpAnalyticsConsent`; a game that asks its own analytics question implements it over that question. |
| `AnalyticsGate`, `AnalyticsEvent` | Holds events until the answer is known, sends them in order once allowed, drops them when refused. The wait is bounded (256 events); a full wait is warned once in every build, and a service whose SDK did not start closes it, so nothing waits for an answer that never comes (issue #171). |
| `AnalyticsRefusals` | Reports each event, parameter or user property Firebase would refuse, once per name and in every build, as `[Firebase] Analytics <what> is not sent: <rule>` (a user property: `is not set`). The Firebase service and the mock share it, so the editor shows the lines a player writes to its log (issue #123). |
| `IRemoteConfigService`, `RemoteConfigFetchPolicy`, `RemoteConfigParser` | `FetchAsync`, typed reads with the game's fallback, a `Changed` event per key, a 12-hour fetch cache, and value parsing with the invariant culture. |
| `ICrashReporter`, `LoggedErrorException`, `CrashContext` | Records an error the game survived as a non-fatal, and the custom keys (`SetCustomKey`) and log lines (`Log`) every later report carries. A key set before the reporter is ready is held and applied once it is, with its last value; so are the newest 64 lines and the newest 32 non-fatals (issue #171). |
| `MockAnalyticsService`, `MockRemoteConfigService`, `MockCrashReporter`, `MockAnalyticsConsent` | In-memory versions for the editor and tests. The analytics mock drops what the Firebase service would drop, a parameter whose value Firebase has no type for included, and reports it the same way; the crash mock keeps the non-fatals, keys and lines (`Recorded`, `Keys`, `Logged`). |
| `FirebaseAnalyticsService` (`NEXUS_FIREBASE_ANALYTICS`) | Analytics behind `IAnalyticsService`. Collection stays off until the decision allows it. |
| `FirebaseRemoteConfigService` (`NEXUS_FIREBASE_REMOTE_CONFIG`) | Remote Config behind `IRemoteConfigService`. A value the console does not set returns the game's fallback. A fetch before Firebase has started fails and says so once in every build. |
| `UnavailableAnalyticsService`, `UnavailableCrashReporter`, `UnavailableRemoteConfigService` | What a player binds for an SDK package it lacks: nothing is sent or reported, and every remote value is the game's fallback (see Binding). |
| `FirebaseBindings.Bind<TConsent>(builder)` | Binds the three services in one call: the Firebase service of each installed SDK in a player, a service that does nothing for a missing one, the mocks in the Editor (see Binding). |
| `FirebaseCollectionFlags` (Editor) | Writes Firebase's collection-off flags into the Android manifest on every build, and fails the build when Firebase is in the project but not as UPM packages (see Consent and the platform manifest). |
| `FirebaseCrashReporter` (`NEXUS_FIREBASE_CRASHLYTICS`) | Crashlytics behind `ICrashReporter`: reports the commands Nexus's recovery engine gave up on, and logged errors (`Debug.LogError`), as non-fatals; custom keys and log lines go to `Crashlytics.SetCustomKey` and `Crashlytics.Log`. It listens from the start of its initialization, so an error logged while the SDK starts is held and reported once it is ready (issue #171). |

## Install the SDK

The adapters were written against Firebase Unity SDK **14.0.0** (`expression` 14.0.0 in the assembly definition) and compiled against it in a disposable project. Google distributes the UPM packages as `.tgz` files, not through a registry: download `com.google.firebase.app`, `.analytics`, `.remote-config` and `.crashlytics` for one version from <https://developers.google.com/unity/archive>, put them in a `GooglePackages` folder next to `Packages`, and add them to `Packages/manifest.json` as `"com.google.firebase.analytics": "file:../GooglePackages/com.google.firebase.analytics-14.0.0.tgz"` and so on. `com.google.firebase.app` depends on the External Dependency Manager (1.2.190), from the OpenUPM registry as for `com.nexus.ads.admob`. All Firebase packages in a project must be the same version.

## Rules

- **Event and parameter names.** Letters, digits and underscores, starting with a letter, at most 40 characters, and no reserved prefix (`firebase_`, `google_`, `ga_`); Google documents these limits for both (the Firebase Android `FirebaseAnalytics.Event` and `FirebaseAnalytics.Param` references). Firebase compares names case-sensitively, so case is kept.
- **Reserved event names.** An event name is also none of the names Google reserves for apps, such as `ad_reward`, `error` and `session_start`: the union of the GA4 event naming rules for Android and iOS (<https://support.google.com/analytics/answer/13316687>) and the names the Android SDK drops (<https://firebase.google.com/docs/reference/android/com/google/firebase/analytics/FirebaseAnalytics.Event>), which differ (issue #122). A parameter may have such a name (`AnalyticsEventName.IsValidParameterName`); GA4's reserved parameter names restrict custom dimensions in the console, not what is sent.
- **User properties.** A name of at most 24 characters with the same shape, not `user_id`, `first_open_time` or `last_deep_link_referrer`; a value of at most 36 characters. A value over the limit is dropped, not truncated.
- **What is refused is reported.** Firebase logs nothing for a name it is never given, so each refused event, parameter (its name, or a value of no Firebase type) and user property is reported once per name, in every build, with the rule it broke: `[Firebase] Analytics event 'ad_reward' is not sent: the name is one Google reserves for events` (`AnalyticsRefusals`, issue #123).
- **Consent.** The consent status does not say which purposes the player accepted: `Obtained` means the form was answered. Analytics are sent when the choice is granted, dropped when it is refused, and held while the choice or the answer is unknown. Where consent is not required, events are sent at once. Where the choice is read from is `IAnalyticsConsent`'s job. `com.nexus.ads.admob`'s `UmpAnalyticsConsent` reads Google's consent form, which answers nothing about analytics, so its choice is always refused (issue #87).
- **Remote config.** Fetched at launch and reused for 12 hours. A clock set back forces a fetch. A value that cannot be read as the requested type returns the fallback. Numbers are read with the invariant culture, so a device language does not change them.

## Binding

```csharp
public void OnConfigure(IContextBuilder builder)
{
    FirebaseBindings.Bind<UmpAnalyticsConsent>(builder);   // com.nexus.ads.admob; or the game's own NexusService<IAnalyticsConsent>
}
```

A player gets the Firebase service of each SDK package the project has. For an SDK package it lacks it gets a service that does nothing (`UnavailableAnalyticsService`, `UnavailableCrashReporter`, `UnavailableRemoteConfigService`), and an error at start names the package: `[Firebase] com.google.firebase.analytics is not installed as a package, so IAnalyticsService sends no event in this player` (issue #171). The Editor (every batch tool, no `google-services.json` needed) gets the mocks. In a player the game's consent reader is bound after the services, so they start Firebase before it asks the User Messaging Platform; in the Editor a `MockAnalyticsConsent` answering `NotRequired` is bound, so the mocks send at once. The reader is named in every build, so it must compile without the Firebase SDK (for example over the User Messaging Platform's status). The SDK defines exist only inside this package's assemblies: game code does not pick SDK or mock with a define of its own.

## Consent and the platform manifest

`SetAnalyticsCollectionEnabled(false)` at start is too late to stop what the SDK collects before the game's code runs. On every Android build `FirebaseCollectionFlags` writes Firebase's own flags, `false`, into the `application` tag of the launcher manifest for each SDK package the project has: `firebase_analytics_collection_enabled`, and `firebase_crashlytics_collection_enabled` (Crashlytics's [opt-in reporting](https://firebase.google.com/docs/crashlytics/android/customize-crash-reports)). `FirebaseAnalyticsService` and `FirebaseCrashReporter` turn collection on once the consent decision allows it. A flag the manifest already carries is left as the game set it, and a second build adds no duplicate. A game that uses the Firebase SDK directly, without these services, turns collection on itself. The build log names the flags it wrote (`[Nexus Firebase] Collection off in the manifest until consent: ...`). The package sees an SDK only as a UPM package (`versionDefines`); Firebase installed from Google's `.unitypackage` (`Assets/Firebase`) gets no define, so the services would not use it and no flag would be written. When an Android build finds the Firebase Analytics or Crashlytics assembly without its package, it fails and names the package to install (issue #171).

## Not checked by 0.1.0

- The adapters compile against 14.0.0; they have not been run in the editor or on a device here, which needs a `google-services.json`.
- The manifest flags are checked in EditMode on a sample manifest; that a device starts with Crashlytics collection off has not been observed yet.
- That Crashlytics reports an error logged while the SDK starts (the held non-fatals of `CrashContext`) has not been observed on a device; `CrashContext`'s holding is tested in EditMode.
- The string length of event parameter values (100 characters for standard properties, per Google's documentation).
- The number of parameters per event and the 500 event types per app.

## Tests

`Tests/Editor` (namespace `Nexus.Firebase.Tests`) runs in the EditMode of the Nexus development project, with no SDK installed, and in the proof project `Nexus/artifacts/firebase-20261031/FirebaseConsumer`, with the SDK packages, where the tests that need `NEXUS_FIREBASE_REMOTE_CONFIG` run too. Results are read from the result file.