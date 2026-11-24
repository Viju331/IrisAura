# Progress

Evidence log, one entry per phase (BLUEPRINT §10). A phase is complete only when every acceptance
criterion has recorded evidence. Never record biometric or credential content here.

Status legend: ✅ done with evidence · ⏳ pending (owner noted) · ❌ failed · ➖ not applicable

---

## Phase 0A — Repository and development environment

**Branch:** `phase-0a` · **Started:** 2026-11-24 · **Status:** ⏳ in progress — C# part complete; native build, VM and tooling wait on installs by the owner.

### Scope delivered

- Solution `IrisAura.sln` (C# + native) and `IrisAura.Managed.slnf` (C# only, for the dotnet CLI), following BLUEPRINT §6 and the names in §0.2.
- Build configuration: `global.json` (SDK 10.0.401, latest patch; Microsoft.Testing.Platform), `Directory.Build.props` (C# only; net10.0-windows10.0.19041.0, C# 14, nullable, warnings as errors, lock files), `Directory.Packages.props` (all approved packages pinned), `nuget.config` (nuget.org only, source mapping), `native/IrisAura.Native.props` (C++20, /W4 /WX, static CRT, CFG, CET).
- Projects: Contracts (netstandard2.0, pipe names + protocol versions), Theme (Brand.xaml tokens §0.4, Glyphs.xaml §0.5), Island (WPF skeleton window, PerMonitorV2 manifest), Island.Providers, Island.Sdk (netstandard2.0), Island.Cli (`irisaura-island`), Face.Lab (WPF skeleton), Face.Core/Vision/Camera/Security (empty), Tools, inert native `IrisAura.Face.CredentialProvider` (no COM classes, no registration exports).
- Tests: `IrisAura.Island.Tests` — architecture rule (Island ⇸ Face), brand tokens vs §0.4, glyph geometry bounds, pipe names vs §0.2 and SID validation.
- Scripts: `build.ps1`, `build-brand-assets.ps1`, `new-dev-cert.ps1`, `generate-dependency-inventory.ps1`.
- Brand: draft simplified icon mark and monochrome tray mark (`assets/brand/source/*.svg`) — **awaiting owner approval**.
- Docs: this file, KNOWN_ISSUES, HARDWARE_MATRIX, SECURITY_REVIEW, THREAT_MODEL, RECOVERY, PRIVACY, ISLAND_SDK (draft), BRAND_LICENSE, VM_SETUP, DEPENDENCIES (generated), DECISIONS 0001–0003.
- Repository basics: README, LICENSE (proprietary), SECURITY, CONTRIBUTING, THIRD_PARTY_NOTICES, CLAUDE.md, .editorconfig, .gitattributes, .gitignore.

### Acceptance criteria

| Criterion (BLUEPRINT §10 Phase 0A) | Status | Evidence |
|---|---|---|
| Clean clone builds (C#) | ✅ | Fresh `git clone` of `phase-0a` into an empty folder, then `scripts/build.ps1 -Locked`: build 0 warnings / 0 errors, 34/34 tests passed (2026-11-24, .NET SDK 10.0.401, Windows 11 build 26300). |
| x64 native project builds | ⏳ owner | Visual Studio C++ tools and Windows SDK are not installed on the dev machine. `build.ps1` detects this and reports `SKIPPED (MSVC not installed)`. Re-run after installing (see "Owner actions"). |
| VM checkpoint/recovery demonstrated | ⏳ owner | Dev machine runs Windows 11 Home (no Hyper-V). Options and steps in [VM_SETUP.md](VM_SETUP.md). |
| Dependency inventory generated | ✅ | [DEPENDENCIES.md](DEPENDENCIES.md) via `scripts/generate-dependency-inventory.ps1` (22 package entries). |
| Model versions, hashes, licences pinned | ➖ | No models yet (Phase 6). `models/SHA256SUMS` and `models/LICENSES/` are in place. |
| Architecture rule enforced | ✅ | `ArchitectureTests`; negative check: adding a `IrisAura.Face.Core` reference to `IrisAura.Island.Providers` made the test fail, then reverted. |

### Owner actions to finish Phase 0A

1. Install **Visual Studio 2026** (Community is fine for this stage) with *Desktop development with C++*, plus the individual components **MSVC ARM64 build tools** and a **Windows 11 SDK**. Then run `./scripts/build.ps1` and record the native result here.
2. Install **ImageMagick 7**, run `./scripts/build-brand-assets.ps1`, and review the generated icons at 100/150/200 % scaling.
3. **Approve or change** the draft icon mark (`assets/brand/source/irisaura-mark.svg`) and tray mark.
4. Set up a disposable Windows 11 VM with a checkpoint and a second admin account ([VM_SETUP.md](VM_SETUP.md)); restore the checkpoint once and record it here.
5. Start **code-signing certificate procurement** (BLUEPRINT §12.4). Meanwhile, `./scripts/new-dev-cert.ps1` creates a test-only certificate.
6. Fill in [BRAND_LICENSE.md](BRAND_LICENSE.md) (who made the logos, under what terms) and run the trademark search (BLUEPRINT §0.7).
7. Decide on WiX licensing (Open Source Maintenance Fee) before Phase 9 (BLUEPRINT §12.2).

### Deviations from the blueprint (recorded as decisions)

- Tests use **xunit.v3 on Microsoft.Testing.Platform**, so `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are not needed ([ADR 0002](DECISIONS/0002-build-and-test-infrastructure.md)).
- C++ settings live in `native/IrisAura.Native.props`, imported after `Microsoft.Cpp.props`, instead of `native/Directory.Build.props`. `Directory.Build.props` is evaluated before the C++ defaults, which would override it ([ADR 0002](DECISIONS/0002-build-and-test-infrastructure.md)).
- WiX banner/dialog bitmaps are deferred to Phase 9 ([ADR 0003](DECISIONS/0003-brand-asset-pipeline.md)).
