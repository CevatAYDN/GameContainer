# Plain Unity sample

Ads in a project without Nexus: one component, no canvas, no code of your own.

1. Import the sample (Package Manager > Nexus Ads > Samples > Plain Unity).
2. Drop `PlainUnityAdsDemo` on any GameObject and press Play. The editor runs the simulator: a
   full-screen overlay with reward, close and fail buttons; the coins go up only on a reward.
3. For a device: **Project Settings > Nexus Ads** creates the settings asset (placements
   `shop_refill` and `level_end`, the provider's keys on the Release tab, your phone's advertising
   id on the Test tab), **Tools > Nexus Ads > Setup** installs the SDK and shows the status. A
   development build serves test ads on listed devices and the simulator elsewhere; a release build
   needs the selected provider to be Active or the build stops.

The component uses only the static `Ads` facade (`IsReady`, `Show`, `PendingReward`,
`AcknowledgeReward`, `PrivacyOptionsRequired`, `ShowPrivacyOptions`). Nothing in it references
`com.nexus.core`.
