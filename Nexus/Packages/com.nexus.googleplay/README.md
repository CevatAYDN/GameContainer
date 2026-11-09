# Nexus Google Play (`com.nexus.googleplay`)

> **Who this is for:** level 3, shipping an Android game on Nexus that asks for a Play Store rating or offers an update from the Play Store.

Google Play's in-app review behind Nexus's `IReviewService` (issue #272) and its in-app update behind `IAppUpdateService` (issue #273), with mocks for the editor. Each Play service compiles only while its Google plugin package is installed (`versionDefines`), like `com.nexus.firebase`. Like Google's plugins, the assembly exists in Android players and the editor only.

## What 0.1.0 contains

| Type | Role |
|---|---|
| `IReviewService`, `ReviewOutcome`, `UnavailableReviewService` (in `com.nexus.core`) | `RequestReviewAsync(ct)` and what it ended with: `Completed`, `Unavailable`, `Failed`. The unavailable service is what a build without a store gets; its first request says so in every build's log. |
| `PlayReviewService` (`NEXUS_PLAY_REVIEW`) | Prepares Google's review flow, launches it, and completes when the sheet closes or Play decides not to show it. Google answers the same in both cases and its policy forbids acting on the answer, so both are `Completed`. A failure is written to every build's log with Google's error code; `PlayStoreNotFound` is `Unavailable`. |
| `MockReviewService` | The editor's service: no sheet, a log line per request, the `Outcome` a test sets (`Completed` by default), and the number of `Requests`. |
| `IAppUpdateService`, `AppUpdateCheck`, `AppUpdateFlow`, `AppUpdateOutcome`, `UnavailableAppUpdateService` (in `com.nexus.core`) | `CheckAsync(ct)` answers whether a newer version is on the store (`AppUpdateAvailability`), its version code, staleness days, priority, which flow is allowed and whether a flexible update is already downloaded; `StartUpdateAsync(flow, ct)` starts the `Immediate` or `Flexible` flow and ends `Downloaded`, `Cancelled`, `Unavailable` or `Failed`; `CompleteUpdateAsync(ct)` installs a downloaded flexible update. The unavailable service answers `StoreUnavailable` and says so once in every build's log. |
| `PlayAppUpdateService` (`NEXUS_PLAY_APPUPDATE`) | Google Play's update flows. Google's `AppUpdateManager` reaches Java when it is made, so the service makes it at the first call. A sideloaded build, a device without the Play Store or its API answers `StoreUnavailable` (a start: `Unavailable`); other errors are written to every build's log with Google's error code; a declined update is `Cancelled`. |
| `MockAppUpdateService` | The editor's update service: no store flow, the `Check` and `Outcome` a test sets (no update and `Downloaded` by default), and the number of `Starts` and `Completions`. |
| `GooglePlayBindings.Bind(builder)` | Binds both services: for each, Google Play's in an Android player with its plugin, the unavailable one with an error naming the plugin in an Android player without it, the mock in the editor. |

## Install the plugins

Google distributes the Play plugins as `.tgz` files on <https://developers.google.com/unity/archive>, not through a registry. The services were written and tested against `com.google.play.review` **1.8.3** and `com.google.play.appupdate` **1.8.3** (`expression` 1.8.3 for each in the assembly definition), which need `com.google.play.core` 1.8.5 and `com.google.play.common` 1.9.2; `com.google.play.core` needs the External Dependency Manager (`com.google.external-dependency-manager`), which resolves the native `com.google.android.play:review:2.0.0` and `com.google.android.play:app-update:2.1.0`. `GooglePlayBindings.Bind` binds both services, so install both plugins. Put the four files in a `GooglePackages` folder next to `Packages` and add them to `Packages/manifest.json`:

```json
"com.google.play.appupdate": "file:../GooglePackages/com.google.play.appupdate-1.8.3.tgz",
"com.google.play.common": "file:../GooglePackages/com.google.play.common-1.9.2.tgz",
"com.google.play.core": "file:../GooglePackages/com.google.play.core-1.8.5.tgz",
"com.google.play.review": "file:../GooglePackages/com.google.play.review-1.8.3.tgz"
```

## Binding

```csharp
public void OnConfigure(IContextBuilder builder)
{
#if UNITY_ANDROID || UNITY_EDITOR
    GooglePlayBindings.Bind(builder);
#else
    builder.BindService<IReviewService, UnavailableReviewService>();
    builder.BindService<IAppUpdateService, UnavailableAppUpdateService>();
#endif
}
```

An Android player with the plugins gets `PlayReviewService` and `PlayAppUpdateService`. One without a plugin gets the core's unavailable service for it, and an error at start names the plugin: `[GooglePlay] com.google.play.review is not installed as a package, so IReviewService shows no rating sheet in this player`, `[GooglePlay] com.google.play.appupdate is not installed as a package, so IAppUpdateService finds no update in this player`. The editor gets `MockReviewService` and `MockAppUpdateService`. The plugin defines exist only inside this package's assembly, so game code does not choose with a define of its own.

## Asking for a review

```csharp
public sealed class AskForReviewCommand : IAsyncCommand<LevelWonSignal>
{
    [Inject] public IReviewService Review { get; set; }

    public async ValueTask ExecuteAsync(LevelWonSignal signal, CancellationToken ct)
    {
        if (signal.Level != 10) return;
        await Review.RequestReviewAsync(ct); // the outcome says only that the flow ended
    }
}
```

Google limits how often the sheet appears, and does not say whether it appeared or what the player rated. Ask at a natural pause, not from a button that promises a rating screen, and never reward or gate anything on the request ([Google's guidelines](https://developer.android.com/guide/playcore/in-app-review#when-to-request)).

## Offering an update

```csharp
public sealed class OfferUpdateCommand : IAsyncCommand<ContextStartedSignal>
{
    [Inject] public IAppUpdateService Updates { get; set; }

    public async ValueTask ExecuteAsync(ContextStartedSignal signal, CancellationToken ct)
    {
        var check = await Updates.CheckAsync(ct);
        if (check.Downloaded)
            await Updates.CompleteUpdateAsync(ct); // installs and restarts the app
        else if (check.Availability == AppUpdateAvailability.UpdateInProgress
                 || (check.Availability == AppUpdateAvailability.UpdateAvailable && check.ImmediateAllowed && check.Priority >= 4))
            await Updates.StartUpdateAsync(AppUpdateFlow.Immediate, ct);
    }
}

// Detached (#155): the context's start does not wait for the store, whose immediate flow may never return.
builder.BindAsyncCommand<ContextStartedSignal, OfferUpdateCommand>(ExecutionMode.Detached);
```

An immediate update covers the game with Play's screen and restarts the app once installed. A flexible one downloads while the game goes on: `StartUpdateAsync(AppUpdateFlow.Flexible, ct)` ends `Downloaded`, and the game asks the player when to restart before it calls `CompleteUpdateAsync`. The priority is set per release through the Play Developer API, not in the Play Console ([Google's guide](https://developer.android.com/guide/playcore/in-app-updates)).

`Downloaded` requires no error and either the SDK's downloaded status or its completed,
full download whose status a late accepted-dialog callback changed to Pending (#389). SDK 1.8.3
can publish failed/canceled status without completing its request; the adapter observes
that terminal status while waiting and returns Failed/Cancelled instead of waiting
indefinitely. Cancelling stops only the wait and detaches its completion handler.
This does not cancel or repair Google's underlying request/manager: repeated manager
ownership and retry after such an SDK-terminal state remain under #391 and device
validation. Do not treat a returned failure as proof that the SDK is ready for another flow.

## Verification

- Compiled and EditMode-tested in a disposable project with the four plugins as `file:` packages: `Nexus/artifacts/googleplay-20261102/`, 9 passed, 0 failed, 0 skipped (the repository's `docs/RELEASE_GATES.md`, Dependencies).
- Controlled SDK 1.8.3 request regressions cover terminal status without a completion
  event, late failed/accepted activity results, normal download and caller cancellation. They
  do not construct the Java-facing manager or launch a store update.
- On a phone the sheet and the update flows appear only for a build installed from Google Play (an internal test track at least); a sideloaded build completes the review without a sheet and finds no store update. The owner chose not to upload one, so neither is observed on a device.
