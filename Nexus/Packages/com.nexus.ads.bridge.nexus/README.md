# Nexus Ads Bridge

Puts the `com.nexus.ads` controller behind `com.nexus.core`'s `AdService`. Game code written against
`IAdService` (`RequestReward`, `ShowInterstitial`, `IsRewardedAvailable`, `ReadyChanged`,
`PrivacyOptionsRequired`, `ShowPrivacyOptions`, `OnImpressionRecorded`, `OnAdImpression`) does not change; the
placements, the policy guard, the test and release tabs and the simulator come from the ads package.
Plan and phases: [issue #8](https://github.com/CevatAYDN/GameContainer/issues/8); changes in the
family's [CHANGELOG](../com.nexus.ads/CHANGELOG.md).

## Install

Three packages by git URL, pinned to the same commit, the full 40-character hash. The bridge declares
`com.nexus.core` and `com.nexus.ads`, and the Package Manager does not install a git package's
dependencies, so each is listed (without the `com.nexus.core` line a bare project stops at
`Package [com.nexus.core@0.6.0] cannot be found`). The ads validator's `manifest.pins` check and
**Nexus > Ads > Pin family packages to one commit** cover the `com.nexus.ads*` entries only;
`com.nexus.core` is set to the same commit by hand:

```json
"com.nexus.core": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.core#<commit>",
"com.nexus.ads": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads#<commit>",
"com.nexus.ads.bridge.nexus": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads.bridge.nexus#<commit>"
```

## Wiring

Bind the service and the factory as before, put an `AdsBootstrap` in the first scene (it loads
`Resources/NexusAdsSettings`, created by **Nexus > Ads > Settings**), and connect once:

```csharp
using Nexus.Ads.Bridge;
using Nexus.Core.Services;

public void OnConfigure(IContextBuilder builder)
{
    builder.BindService<IAdService, AdService>();
    builder.BindInstance<IAdAdapterFactory>(new AdAdapterFactory());
}

public ValueTask OnStartAsync(CancellationToken ct)
{
    // store and product are optional: with them, owning "remove_ads" stops interstitials and banners and keeps rewarded ads;
    // while the store has not said whether it is owned (offline, before its purchase fetch), interstitials and banners stop too (#183)
    NexusAdsBridge.Connect(_context.Resolve<IAdService>(), _context.Resolve<IAdAdapterFactory>(), _context.Resolve<IIapService>(), "remove_ads");
    return default;
}
```

`Connect` registers the adapter under `nexus-ads` and under the running provider's name, sets the
service's interstitial cooldown to the policy's interstitial floor, forwards revenue to
`AdService.RaiseImpression` (so `OnAdImpression` fires with the network, the placement, the format,
the value, the currency and the precision, and `OnImpressionRecorded` with the network, the value and
the placement; issue #135), and completes on the bootstrap's `Started` event when the bootstrap has not fetched the
device id yet.

| `IAdService` member | Through the bridge |
|---|---|
| `IsRewardedAvailable`, `IsInterstitialAvailable` | `AdController.IsReady`: loaded, visible, not paced out by the guard, kill switch off, not ad-free (interstitial) |
| `RequestReward(placement, Action<AdOutcome>)` | `Rewarded`, `Closed`, `Failed`; `NotReady` for nothing loaded and for a guard block (the block is a `PolicyViolation` on the controller, logged in development builds) |
| `ShowRewarded(placement, Action<bool>)` | true only on `Rewarded` |
| `ShowInterstitial` | the guard's natural-break and frequency rules, then the SDK |
| `ShowBanner(placement, position)` | `"top"` or anything else = bottom |
| `ReadyChanged` | the controller's, which also fires when a pacing window runs out (a cooldown, a first-show delay, an interstitial gap, a day cap at the UTC day change; issue #95); follow it instead of polling the two `Is…Available` members. `AdService` also raises it once when its own interstitial cooldown ends (issue #99); that cooldown starts at the show call and the controller's gap at the close, so in that handler `IsInterstitialAvailable` can still be false until the controller's event |
| `PrivacyOptionsRequired`, `ShowPrivacyOptions` | the consent flow the bootstrap runs before the mediation: Google UMP for AdMob; for LevelPlay the one the ad settings name (issue #154): Google UMP from `com.nexus.ads.admob`, the game's own flow, or with `None` LevelPlay's pass-through, which never asks; the simulator's switch in the editor; none on a host that runs no ads |
| `OnAdImpression` | every `AdRevenue` the mediation reports, as an `AdImpression` with its format (`"Rewarded"`, `"Interstitial"`, `"Banner"`), currency and precision; log revenue from this one, since an AdMob impression's currency is not always USD (issue #135) |
| `OnImpressionRecorded` | the same impressions with the network, the value and the placement only |

Remote configuration: with `com.nexus.firebase` installed, `Nexus.Ads.Bridge.Nexus.Firebase`
compiles `FirebaseAdsRemoteConfig` over `IRemoteConfigService` (`ads.kill_switch` plus the
`AdPolicy.Keys` names; remote can only tighten). Attach it with
`AdsBootstrap.Instance.Controller.SetRemoteConfig(new FirebaseAdsRemoteConfig(remote))`.

A paid reward is written to the store before the game hears of it, and the adapter acknowledges
it once the game's `RequestReward` or `ShowRewarded` callback returned, so a receipt still pending
at start-up is a reward whose callback never ran or threw (the app was killed in between). Check
the static facade's `Ads.PendingReward` at start-up (it is read before the ads start), pay it, then
`Ads.AcknowledgeReward(receipt.Value.Id)`. `IAdService` does not carry the receipt: a game whose
reward carries data of its own (an amount captured at the offer, a target) keeps that data itself.

## Confirmed Reward Saves

Confirmation is **opt-in** (issue #338). The old `IPlayerPrefsService.Save()` still returns void;
`EncryptedStorageService` still logs a failed write and retains it for retry. A normal callback
return still acknowledges its receipt, even if the callback used void `Save()` and the write failed.
`AutoSave` alone does not confirm persistence either.

Use `SaveOrThrow()` before returning from either callback form. It calls the additive `TrySave()`;
`EncryptedStorageService` confirms only when all pending writes/deletes committed, with no
superseding mutation or disposal during confirmation. Failed or mixed batches return false.
Unsupported/custom stores default to false, without inferring success from their void `Save()`.
`SaveOrThrow()` throws `IOException` on false, preserving the existing throwing-callback behavior:
the receipt remains pending. Handle that failure at the game's delivery boundary and retry from
the pending receipt; do not acknowledge in a `finally` block.

Grant and confirm directly inside the callback, or in a synchronous helper it calls. Do not
delegate the grant to `SignalBus` or another listener dispatcher that can catch/swallow listener
exceptions: a swallowed save failure makes the outer callback return normally and acknowledge.
The confirmation failure must escape the callback boundary itself. Dispatch secondary reward
notifications only after the confirmed grant.

**This is not a transaction across arbitrary keys.** For exactly-once application across restart,
the game state and applied receipt id must be in **one atomically replaced blob**. Separate balance
and receipt keys can partially commit. The example below uses one JSON wallet and the controller's
public receipt API. For startup/replay, `Ads.PendingReward` / `Ads.AcknowledgeReward(id)` are
the facade equivalent. Inside a live reward callback, use `AdController.DeliveringReward`:
this is the receipt for that delivery, captured before telemetry, close, or readiness handlers
can trigger another show. `PendingReward` can already refer to a newer reward at that point.
`DeliveringReward` is callback-scoped (copy it synchronously), null for non-reward results or a
failed receipt write, and restored after a nested callback even if that callback throws.
The example reconciles the single pending receipt before accepting another reward. A last-receipt field is
not a general historical receipt ledger, and the ads store keeps only one pending receipt.

```csharp
using System;
using System.IO;
using Nexus.Ads;
using Nexus.Core.Services;
using UnityEngine;

public sealed class DurableRewards
{
    [Serializable]
    public sealed class Wallet
    {
        public int Balance;
        public string LastReceiptId;
    }

    private readonly IPlayerPrefsService _save; // a supported store, e.g. EncryptedStorageService
    private readonly IAdService _ads;
    private readonly AdController _controller; // AdsBootstrap.Instance.Controller after startup

    public DurableRewards(IPlayerPrefsService save, IAdService ads, AdController controller)
    {
        _save = save;
        _ads = ads;
        _controller = controller;
    }

    private void Grant(RewardReceipt receipt)
    {
        var status = _save.TryGetString("wallet", out string json);
        if (status == StorageReadStatus.Unreadable) throw new IOException("Wallet is unreadable.");
        var wallet = status == StorageReadStatus.Absent ? new Wallet() : JsonUtility.FromJson<Wallet>(json);
        if (wallet == null) throw new IOException("Wallet is invalid.");
        if (wallet.LastReceiptId != receipt.Id)
        {
            wallet.Balance += 10; // this game's fixed shop_refill grant
            wallet.LastReceiptId = receipt.Id;
            _save.SetString("wallet", JsonUtility.ToJson(wallet));
        }
        _save.SaveOrThrow(); // also required on an idempotent replay of a prior committed grant
    }

    public void ReplayPending()
    {
        var receipt = _controller.PendingReward;
        if (!receipt.HasValue) return;
        Grant(receipt.Value); // failure leaves the receipt pending
        _controller.AcknowledgeReward(receipt.Value.Id);
    }

    private void GrantCurrent()
    {
        var receipt = _controller.DeliveringReward;
        if (!receipt.HasValue) throw new IOException("Reward receipt is unavailable.");
        Grant(receipt.Value); // adapter acknowledges this captured receipt only after normal return
    }

    public void Request()
    {
        ReplayPending(); // do not start another show if reconciliation failed
        _ads.RequestReward("shop_refill", outcome => { if (outcome == AdOutcome.Rewarded) GrantCurrent(); });
    }

    public void Show()
    {
        ReplayPending();
        _ads.ShowRewarded("shop_refill", rewarded => { if (rewarded) GrantCurrent(); });
    }
}
```

If the blob commit succeeded but acknowledgement did not (including process death in between),
reopen the wallet and replay the same receipt: its id prevents a second balance increment, then
acknowledgement clears it. If the blob write failed, reopen sees the previous wallet and the pending
receipt supplies the missing grant. Keep reward delivery serialized and recover unreadable/invalid
wallets explicitly instead of resetting them. These guarantees depend on the blob store's atomic
commit and the receipt store's durable persistence; `TrySave()` adds no durable guarantee to
unimplemented stores and does not make two live caches over the same files safe.

`TrySave()` confirms successful OS/filesystem operations at the commit check, **not** an
`fsync`/stable-media or sudden-power-loss guarantee. The encrypted store does not force file and
directory data to stable media; a power failure, OS crash, or storage failure can still lose a
recently acknowledged reward. The replay example and host tests cover abrupt process reopen
against the files the OS retained, not physical power-cut durability. Stronger guarantees require
a store with the corresponding durability contract; no Unity/device or power-loss proof is
claimed by these host checks.

## Tests

`Tests/Editor/NexusAdsBridgeContractTests` drive `AdService` through the adapter over the simulator
with hand-written ports: the capability rows of `com.nexus.core/docs/ADAPTERS.md` (outcomes,
readiness, privacy options, the cooldown floor, revenue, the ad-free entitlement, the outcome map).
`Tests/Runtime/NexusAdsBridgePlayModeTests` connect before the bootstrap started and take a reward
through the service over the editor's simulator overlay, and connect to a release host that runs no ads:
its controller stays Stopped and a reward request answers `NotReady` (issue #165).
`Tests/Editor/DurableRewardDeliveryTests` uses real encrypted game-state files, a separate writable
receipt store, and the simulator through the real adapter. It checks failed saves, abrupt reopen,
startup replay, commit-before-ack replay, both callbacks, legacy behavior, non-reward outcomes, and
reentrant receipt capture. The host compiles the same fixture and real adapter/storage sources.
