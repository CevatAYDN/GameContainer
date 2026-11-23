# iOS port guide

Porting a Nexus game to the App Store, written from Wickling's first submission (2026-11-15 to 2026-11-20, issue #436) so that the next game takes days, not a week. It assumes the stack the Nexus games share: Unity 6000.6, LevelPlay with Unity Ads, Unity IAP, the External Dependency Manager, Firebase in some games.

> **Status.** The iOS App Store kit (`Nexus.Editor.Ios`, in `com.nexus.core`'s `Editor/Ios`) was written without Unity and without `dotnet`: nothing in it has been compiled or run. Its pure rules have host-harness tests (`FullReviewIosKitTests`) that have not run either. No Xcode project has been exported with it and no archive has been checked. Treat the first export of a game as the proof, and compare it with the checks below by hand once.

Steps with lead time (Apple, LevelPlay) start on day 1, beside the code.

## Day 1: accounts (the account holder, in parallel with the code)

1. **App Store Connect app record**: bundle id the same as the Play package id, an SKU, a primary language. One record per game.
2. **Paid Apps Agreement** (Business): legal entity, bank, tax. Only the account holder types bank and tax details. No IAP price loads anywhere until it is Active, and the sandbox saw it about a day after.
3. **EU DSA trader declaration** (Business > Compliance): an address document in English; "In Review" does not block submission.
4. **Sandbox tester** (Users and Access > Sandbox): an address that is not an Apple Account (a `+sandbox1` alias works). The tester signs in on the phone only when a purchase sheet asks.
5. **LevelPlay iOS app**: add the iOS app, then Setup > Instances > Unity Ads: Game ID = the Unity Ads **Apple** Game ID (not the Google one) and an instance per format with the Unity placement ids. An empty Unity Ads instance shows as the network with no check marks under Rewarded; Unity Ads is the only fill for a new account.
6. **App Review contact**: a real name, phone with country code and e-mail on the version page.

## The kit: what turns on in the project

Create one settings asset: **Assets > Create > Nexus > iOS App Store Kit** (`IosKitSettings`). Without it the post-process does nothing.

| Setting | Becomes |
|---|---|
| `declaresTracking` | The game's statement on the App Store label. **False** (the default, and what the Nexus ads package assumes: it never requests tracking) removes `NSUserTrackingUsageDescription`, refuses `NSPrivacyTracking`, tracking domains and tracking data types, and makes the ship check refuse a tracking text. True requires the text in Player Settings. |
| `languages` | `CFBundleLocalizations`, and for each entry with a display name `<code>.lproj/InfoPlist.strings` (added to the Xcode project). |
| `developmentRegion` | `CFBundleDevelopmentRegion`, when the plist has none. |
| `dataTypes` | `NSPrivacyCollectedDataTypes` of `PrivacyInfo.xcprivacy`: the same list the App Store privacy label has, each with its purposes. |
| `accessedApis` | `NSPrivacyAccessedAPITypes`: the required-reason APIs with their reason codes. |
| `podsMinimumIos` | A Podfile `post_install` that builds every pod for that iOS (once; a game's own `post_install` gets the loop inside it). |

The post-process (`IosPostProcess`, order 950: after the packages', before a game's own 999) also writes `ITSAppUsesNonExemptEncryption` false when the game set none, `CFBundleDisplayName` from the product name, and `PrivacyInfo.xcprivacy` on every export (an appended Xcode project would otherwise keep a stale one). An invalid privacy declaration or a declared-tracking game without the usage text **fails the build**.

### The privacy manifest and ITMS-91054

`NSPrivacyAccessedAPIType` takes a **category** (`NSPrivacyAccessedAPICategoryUserDefaults`, `...FileTimestamp`, `...SystemBootTime`, `...DiskSpace`, `...ActiveKeyboards`), never a type name; Apple reports a wrong value only by e-mail to the account holder after upload (ITMS-91054, "Invalid Binary" in App Store Connect). Each category needs at least one reason code of the form `CA92.1`. The kit checks the category against the five constants and the reason against the form, not against Apple's list of reason codes: that list was not readable when the kit was written, so take the codes from Apple's page "Describing use of required reason API" and keep the manifest equal to what the game and its SDKs use. The data-type and purpose constants are checked by their `NSPrivacyCollectedDataType` / `NSPrivacyCollectedDataTypePurpose` prefix only. The manifest, the App Store privacy label and the privacy policy page change together.

Firebase's launch-time "collection off" keys are written by `com.nexus.firebase` (issue #434), not by the kit.

### Simulator build (store screenshots)

`Nexus.Editor.Ios.IosSimulatorBuild.Build(path)` (or `-executeMethod Nexus.Editor.Ios.IosSimulatorBuild.BuildCli -iosSimulatorOutput <folder>`) builds the enabled scenes with `iOSSdkVersion.SimulatorSDK`, always as a Development build (Unity 6000.6 ships only a development Simulator runtime), and puts the SDK and development settings back afterwards. It does not set the Simulator architecture (Apple silicon builds arm64 by default). Capture with `xcrun simctl io <udid> screenshot --mask ignored`.

### Ship checks (after the archive)

```
Unity -batchmode -nographics -quit -projectPath <project> -executeMethod Nexus.Editor.Ios.IosShipCheckCli.Run \
  -iosArchive <Game.xcarchive> -iosProject <exported Xcode folder> -iosLastBuild <last uploaded build> -iosRequireClass ISUnityAdsAdapter
```

Exit 0 all checks pass, 1 a check failed (do not export), 2 could not run. macOS only (`plutil`, `xcodebuild`). It reads the archive itself, not the settings that should have produced it:

- `ITSAppUsesNonExemptEncryption` is false;
- `NSUserTrackingUsageDescription` is there only when tracking is declared;
- `FIREBASE_ANALYTICS_COLLECTION_ENABLED` and `FirebaseCrashlyticsCollectionEnabled` are false when the Firebase SDK is in the project;
- `PrivacyInfo.xcprivacy` is in the app bundle and valid;
- UnityFramework's `OTHER_LDFLAGS` holds `-ObjC` (added by `com.nexus.ads.levelplay`, issue #435);
- every `-iosRequireClass` name is in the `UnityFramework` binary (LevelPlay creates its adapters by class name; list one `IS*Adapter` per `Assets/LevelPlay/Editor/IS*AdapterDependencies.xml`);
- `CFBundleVersion` is above `-iosLastBuild`: a build number once uploaded is spent.

## Code checklist

| Step | Where |
|---|---|
| iOS build target, bundle id, team id from the environment (never in git), automatic signing | the game's build script |
| `com.unity.external-dependency-manager` in the manifest (without it the Podfile is empty) | `Packages/manifest.json` |
| `-ObjC` on UnityFramework | `com.nexus.ads.levelplay` (#435) |
| Info.plist, privacy manifest, localizations, Pods minimum | the kit, above |
| Firebase collection off at launch | `com.nexus.firebase` (#434) |
| Consent without AdMob/UMP | the ads package's game-drawn consent flow (#430) |
| App icon set in the iOS build, or Unity's logo ships | the game |
| Tablets: UI Toolkit panel scale **Expand** from a phone reference; Shrink breaks phones | the game's PanelSettings |
| Raise the iPhone `buildNumber` for every upload | `ProjectSettings` |

## Hard gate before upload: on a phone

A fresh install of the release build, on a real device:

- the log (`idevicesyslog -p <App>`) shows analytics held until the consent answer, then sent;
- the consent screen with real text, Accept;
- **a real rewarded ad plays** ("no ad" here is a setup gap: day 1 step 5, or a missing adapter, never "the app is not live yet");
- the IAP price loads, a sandbox purchase works, and after uninstall and reinstall **Restore purchases** returns it.

A release build cannot target the Simulator on Unity 6000.6; check release behaviour on a phone. The kit's checks do not replace this gate and none of it has been run through the kit yet.

## App Store Connect (the listing)

- **Screenshots**: the required iPhone slot is **6.3"** (1206 x 2622), plus iPad 13" (2064 x 2752) when the app runs on iPad.
- **Name** 30 characters, subtitle 30, keywords 100 **bytes** (non-ASCII letters count 2). Each localization needs its own texts and screenshots.
- **Age rating**, pricing, Mac and Vision Pro off unless tested.
- **IAP (non-consumable)**: display name and description limits per language; a review screenshot of the purchase screen **with the price showing**; review notes saying where the button and Restore are.
- **The first IAP is submitted with a version**: the version page has no IAP section. "Add for Review" on the IAP page, then choose the same draft submission on the version page and submit.
- **Manual release**, so the steps after approval run first (Unity Ads Apple Store ID, LevelPlay app to Live).

## Time sinks to skip

- ITMS-91054 (above): write the manifest on every export and let the ship check read it from the archive.
- Unity archives placements that get no requests for a week; add a new iOS ad unit and placement and use its id in the LevelPlay instance.
- A LevelPlay test-mode ad is not proof for release.
- The Unity dashboard's "missing SKAdNetwork IDs" banner lists its whole list; compare with the build, it is revenue, not approval.
