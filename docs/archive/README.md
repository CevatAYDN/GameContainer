# Archived reports

Dated audits, reviews, roadmaps and status snapshots. They are kept as evidence of what was
found and fixed, and are **not** maintained: numbers, file paths and status claims in them
describe the code at their date. Current state lives in the package documentation
(`Nexus/Packages/com.nexus.core/README.md`, `docs/`) and in `docs/RELEASE_GATES.md`.

| File | Date | What it was | Superseded by |
|---|---|---|---|
| `NEXUS_READY.md` | 2026-08-04 → 2026-08-13 | Production-readiness gates with the numbers of that time (86 runtime files, CI status) | `../RELEASE_GATES.md` |
| `AUDIT_REPORT.md` | 2026-08-05 | Adversarial runtime audit (97 files at the time) | Fixes recorded in the package `CHANGELOG.md` ("Fixed (Adversarial Audit - 2026)") |
| `AUDIT-2026-08-04.md` | 2026-08-04 | Audit of the 0.4.0 code with status banners added on 2026-08-06 | Package `CHANGELOG.md` |
| `AUDIT-2026-08-13.md` | 2026-08-13 | Audit follow-up (trace logging leftovers, wizard template sync, storage fallbacks) | Package `CHANGELOG.md` ("Fixed (2026-08-13 audit follow-up)") |
| `REVIEW-2026-08-13.md` | 2026-08-13 | Semantic review R1–R5 | Package `CHANGELOG.md` ("Fixed (2026-08-13 semantic review)") |
| `STUDIO-EVALUATION-2026-08-13.md` | 2026-08-13 | Studio-adoption evaluation with P0/P1 gaps | `../research/COMPETITORS_2026-10-15.md` for the market view; open gaps tracked in `../RELEASE_GATES.md` |
| `COMPETITIVE_ROADMAP-2026-08-13.md` | 2026-08-13 | Competitor matrix and seven-phase roadmap. Its §3 matrix and §9 corrected matrix disagree with each other (VContainer editor tooling); Reflex is absent | `../research/COMPETITORS_2026-10-15.md` |
| `REFACTOR_PLAN.md` | 2026-08 | Boot and frame-spike refactor plan with implementation-status banner | Package `CHANGELOG.md` ("Performance") |
| `NEXUS_REVIEW_2026_10_07.md` | 2026-10-07 | Commit review LC01–LC20 with verified evidence (EditMode 352, PlayMode 160, harness 291) | `HARDENING.md` (archived beside it); numbers refreshed in `../RELEASE_GATES.md` |
| `NEXUS_PERFORMANCE_2026_10_07.md` | 2026-10-07 | Optional-map startup optimisation and Unity Mono player DI comparison | Package `docs/PERFORMANCE_COMPARISON.md` |
| `NEXUS_NEXT_STEPS_2026_10_07.md` | 2026-10-07 | Prioritised next steps after the October review | `../RELEASE_GATES.md` (open gates) |
| `package-reviews/DEEP_CODE_REVIEW_2026.md` | 2026 | Deep code review that used to ship inside the UPM package | Package `CHANGELOG.md` |
| `package-reviews/REVIEW_FINDINGS_A1_B8.md` | 2026-08-01 | Findings A1–A6, B1–B8 with per-finding fix evidence | Package `CHANGELOG.md` |
| `package-reviews/REVIEW_VALIDATION.md` | 2026-07-31 | Validation of four action items from a July review | Package `CHANGELOG.md` |
| `package-reviews/SERVICE_AUDIT.md` | 2026-07-31 | Audit of the service layer (13 services at the time; 16 `NexusService` subclasses on 2026-10-25) | Package `README.md` service table |
| `package-reviews/NEXUS_NOTEBOOKLM_ADVERSARIAL_AUDIT_REGISTRY.md` | 2026 | Registry of externally generated audit claims and their verdicts | Package `CHANGELOG.md` |
| `HARDENING.md` | 2026-10-06 → 2026-10-07 | Correctness, integration and performance hardening R01–R20 with the verification of that time (harness 291, EditMode 352, PlayMode 160); it shipped in the package `docs/` until 2026-10-25 | `../RELEASE_GATES.md` |
| `SLO.md` | 2026-08 → 2026-10-15 | Service-level objectives with harness "baselines" no run records, and a CI gate, self-hosted runner, crash reporting and device soak that did not exist | `../RELEASE_GATES.md`; the package README's Performance scope table |
| `INTEGRATION.md` | 2026-08 | Turkish integration guide (UPM, submodule, npm) with Ads/IAP adapter registration examples that use pre-Unity-6 APIs | Package `docs/GETTING_STARTED.md`, `docs/INTEGRATION_PATHS*.md`; adapters: package `docs/ADAPTERS.md` |
| `RELEASE_GATES-2026-10-28.md` | 2026-10-15 → 2026-10-28 | Release gates with the per-commit history from v0.5.0 (`8f19c20`) until the cycle on `3271239` (the games' target was then `c7d5bf2`), the device readings of #77 and #84 and the ad family's earlier test counts included | `../RELEASE_GATES.md` |

Relative links inside archived files were written for their original locations and may not resolve.
