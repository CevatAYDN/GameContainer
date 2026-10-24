# Nexus Ads

The ad package family's core: an engine-independent ad core, a Unity layer, an editor setup wizard
and validator, and a simulator provider. Mediation SDKs live in separate provider packages
(`com.nexus.ads.levelplay`, `com.nexus.ads.max`, `com.nexus.ads.admob`); Nexus games connect through
`com.nexus.ads.bridge.nexus`. The plan, decisions and phases are in
[issue #8](https://github.com/CevatAYDN/GameContainer/issues/8).

Phase 1 ([#9](https://github.com/CevatAYDN/GameContainer/issues/9)) ships two things:

- [docs/POLICY_COMPLIANCE.md](docs/POLICY_COMPLIANCE.md) ([Türkçe](docs/POLICY_COMPLIANCE_TR.md)): per platform the official policy pages, the day they were read, the rules that apply to a mobile game and where each rule is covered in the package (code, validator, developer checklist).
- [Editor/PlatformCatalog.json](Editor/PlatformCatalog.json): registration and documentation links, official test data with its source, SDK distribution (UPM package names, registries, defines) and setup steps in English and Turkish, read by the setup wizard.

No runtime code yet; the core, PolicyGuard and simulator arrive with Phase 2
([#10](https://github.com/CevatAYDN/GameContainer/issues/10)).
