# Nexus sample

Ads in a Nexus game: the services of `com.nexus.core`, the bridge of `com.nexus.ads.bridge.nexus`,
an `AdsBootstrap` in the scene, and game code that talks to `IAdService` only.

1. Install `com.nexus.ads` and `com.nexus.ads.bridge.nexus` at the same commit (the bridge README
   has the manifest lines) and import this sample.
2. Create the settings asset in **Project Settings > Nexus Ads** with a rewarded placement named
   `shop_refill`.
3. Put `AdsBootstrap` and `NexusAdsSample` on the scene's Root object and press Play. The lifecycle
   binds `AdService` and the factory, `NexusAdsBridge.Connect` hands the controller to the service on
   the bootstrap's `Started` event, and the button pays through `IAdService.RequestReward` over the
   editor's simulator overlay.

The sample's lifecycle is the one in `com.nexus.core/docs/ADAPTERS.md`, with `NexusAdsBridge.Connect`
in place of a hand-registered SDK adapter.
