# Nexus Unity Mobile Notifications Adapter (`com.nexus.notifications.unity`)

> **Who this is for:** level 3, shipping an Android game on Nexus that reminds a player who has left.

Unity Mobile Notifications on Android behind Nexus's `INotificationService`, with a mock for the editor (issue #160). Like the notifications package's Android API, the assembly exists in Android players and the editor only.

## What 0.1.0 contains

| Type | Role |
|---|---|
| `INotificationService`, `NotificationPermission`, `UnavailableNotificationService` (in `com.nexus.core`) | `Permission` (`Allowed`, `NeedsPermission`, `Blocked`, `Unavailable`), `RequestPermissionAsync(ct)`, `OpenSettings()`, `SetChannel(id, name, description, smallIcon)`, `Schedule(title, text, fireTime, data)` returning the notification's id, `Cancel(id)`, `CancelAll()` (scheduled and shown) and `TakeOpenedWith()`. The unavailable service is what a build without a provider gets; its first call says so in every build's log. A schedule before `SetChannel` throws in every implementation. |
| `UnityNotificationService` (`NEXUS_UNITY_NOTIFICATIONS`) | One channel of default importance; a tap opens the app and removes the notification. `RequestPermissionAsync` shows Android's dialog only when Android needs and still allows one, and completes with the answer. |
| `MockNotificationService` | The editor's service: nothing is posted, a log line per scheduled notification, and the state a test sets: `Permission` (allowed by default, and what a request answers), the `Scheduled` list, the `OpenedWith` data the next `TakeOpenedWith` returns, and the number of `PermissionRequests` and `SettingsOpened`. |
| `UnityNotificationBindings.Bind(builder)` | Binds the service: Unity Mobile Notifications in an Android player, the core's unavailable service with an error when the notifications package is older than 2.5.0, the mock in the editor (the package's notification center adds a scene object there at its first call). |

## Install

This package by git URL, pinned to a commit of the repository (the full 40-character hash):
`https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.notifications.unity#<commit>`.
It depends on `com.unity.mobile.notifications` 2.5.0 (installed by the Package Manager), whose build step adds Android's `POST_NOTIFICATIONS` permission and its receiver to the app's manifest. A notification's small icon is an identifier registered in **Project Settings > Mobile Notifications**; without one Android shows the app icon.

## Binding

```csharp
public void OnConfigure(IContextBuilder builder)
{
#if UNITY_ANDROID || UNITY_EDITOR
    UnityNotificationBindings.Bind(builder);
#else
    builder.BindService<INotificationService, UnavailableNotificationService>();
#endif
}
```

## Reminding a player who left

```csharp
public sealed class RemindersMediator : Mediator<RemindersView>
{
    [Inject] public INotificationService Notifications { get; set; }

    protected override void OnBind()
    {
        Notifications.SetChannel("progress", "Progress", "When the lab finishes", smallIcon: "notify_small"); // again after a language change
        Subscribe<ApplicationPauseSignal>(OnPause);
        if (Notifications.TakeOpenedWith() == "research") View.OpenResearch(); // the data of the notification a tap opened the app with
    }

    private void OnPause(ApplicationPauseSignal signal)
    {
        if (!signal.IsPaused)
        {
            Notifications.CancelAll(); // a player who is back needs no reminder
            return;
        }
        if (Notifications.Permission == NotificationPermission.Allowed)
            Notifications.Schedule("Research done", "The lab finished its work.", DateTime.Now.AddHours(2), "research");
    }
}
```

`ApplicationPauseSignal` is fired synchronously because Android may freeze the process when the pause returns, so the schedule is made in the handler itself. Android may deliver a notification a few minutes late (the package's inexact scheduling).

## Asking for the permission

Android 13 and later ask the player once the game requests it:

```csharp
var answer = await Notifications.RequestPermissionAsync(ct);
if (answer == NotificationPermission.Blocked) Notifications.OpenSettings();
```

Ask when the player sees why a reminder helps, not at the first start. Android shows its dialog at most twice: after two refusals a request answers `NeedsPermission` without a dialog, and a notification turned off in the phone's settings answers `Blocked`. In both cases only the phone's settings page (`OpenSettings`) can turn notifications on, so the game counts its own requests and offers that page instead.

## Verification

- The package is embedded in the repository's development project, so its declared notifications package resolves there: EditMode 769 passed, 0 failed, 1 skipped of 770, the package's four tests among them; the Android IL2CPP player's merged manifest holds the permission and the package's receiver (`Nexus/artifacts/notifications-20261102/`; the repository's `docs/RELEASE_GATES.md`, Dependencies).
- An Android build must start the editor on Android (`-buildTarget Android` in batch mode): the notifications package adds its permission and receiver from editor code compiled only there.
- A notification posted on a phone is not observed yet.
