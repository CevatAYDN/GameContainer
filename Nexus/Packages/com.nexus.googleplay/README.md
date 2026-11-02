# Nexus Google Play (`com.nexus.googleplay`)

> **Who this is for:** level 3, shipping an Android game on Nexus that asks for a Play Store rating.

Google Play's in-app review behind Nexus's `IReviewService`, with a mock for the editor (issue #272). The Play service compiles only while Google's plugin package is installed (`versionDefines`), like `com.nexus.firebase`. Like Google's plugins, the assembly exists in Android players and the editor only.

## What 0.1.0 contains

| Type | Role |
|---|---|
| `IReviewService`, `ReviewOutcome`, `UnavailableReviewService` (in `com.nexus.core`) | `RequestReviewAsync(ct)` and what it ended with: `Completed`, `Unavailable`, `Failed`. The unavailable service is what a build without a store gets; its first request says so in every build's log. |
| `PlayReviewService` (`NEXUS_PLAY_REVIEW`) | Prepares Google's review flow, launches it, and completes when the sheet closes or Play decides not to show it. Google answers the same in both cases and its policy forbids acting on the answer, so both are `Completed`. A failure is written to every build's log with Google's error code; `PlayStoreNotFound` is `Unavailable`. |
| `MockReviewService` | The editor's service: no sheet, a log line per request, the `Outcome` a test sets (`Completed` by default), and the number of `Requests`. |
| `GooglePlayBindings.Bind(builder)` | Binds the service: Google Play's in an Android player with the plugin, the unavailable one with an error naming the plugin in an Android player without it, the mock in the editor. |

## Install the plugins

Google distributes the Play plugins as `.tgz` files on <https://developers.google.com/unity/archive>, not through a registry. The service was written and tested against `com.google.play.review` **1.8.3** (`expression` 1.8.3 in the assembly definition), which needs `com.google.play.core` 1.8.5 and `com.google.play.common` 1.9.2; `com.google.play.core` needs the External Dependency Manager (`com.google.external-dependency-manager`), which resolves the native `com.google.android.play:review:2.0.0`. Put the three files in a `GooglePackages` folder next to `Packages` and add them to `Packages/manifest.json`:

```json
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
#endif
}
```

An Android player with the plugin gets `PlayReviewService`. One without it gets `UnavailableReviewService`, and an error at start names the plugin: `[GooglePlay] com.google.play.review is not installed as a package, so IReviewService shows no rating sheet in this player`. The editor gets `MockReviewService`. The plugin defines exist only inside this package's assembly, so game code does not choose with a define of its own.

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

## Verification

- Compiled and EditMode-tested in a disposable project with the three plugins as `file:` packages: `Nexus/artifacts/googleplay-20261102/`, 5 passed, 0 failed, 0 skipped (the repository's `docs/RELEASE_GATES.md`, Dependencies).
- On a phone the sheet appears only for a build installed from Google Play (an internal test track at least); a sideloaded build completes without a sheet. The owner chose not to upload one, so the sheet on a device is not observed.
