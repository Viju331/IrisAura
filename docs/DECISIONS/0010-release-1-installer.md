# 0010 — Release 1: installer, signing and update verification

- **Status:** accepted (production signing pending)
- **Date:** 2026-11-26
- **Phase:** 5 (Release 1 gate; the Island subset of Phase 9)
- **Blueprint sections:** §4, §10 Phase 5 / Phase 9, §12.1–§12.5, §13

## Context

Release 1 needs an Island-only installer that installs, upgrades and uninstalls cleanly, signed binaries, licence
notices and update verification. Owner decisions (2026-11-26): use **WiX** and pay its Open Source Maintenance Fee;
run install tests **on DEV-01** with UAC approval; sign with the **development certificate** until a production
certificate is bought; then continue with Phase 0B.

## Decision

1. **WiX Toolset 7.0.1** (`WixToolset.Sdk`, pinned in `global.json` `msbuild-sdks`; added to the BLUEPRINT §4 approved
   list). The OSMF EULA is accepted in the project (`AcceptEula=wix7`) on the owner's decision; the fee applies to
   revenue-generating use above US$10,000/year. No WiX extension or custom-action DLL is used, so no WiX binaries
   ship inside the MSI.
2. **Per-machine MSI** `installer/IrisAura.Msi`: `%ProgramFiles%\IrisAura\Island`, Start-menu shortcut, CLI folder on
   the system PATH, licence notices (`LICENSE.txt`, `THIRD_PARTY_NOTICES.txt`, `licenses\`). Island only: no Face
   files, services or credential provider (checked by the install test). Major upgrades; ICE38/43/57 suppressed with
   the per-machine justification.
3. **Self-contained, ReadyToRun** app (no .NET install needed; faster start); the CLI as a compressed single file.
   MSI ≈ 101 MB. x64 only for now (KI-048).
4. **Start at sign-in via the HKLM Run key**, not the scheduled task of §12.1: native to MSI (removed on uninstall,
   no custom action), and users can switch it off in Task Manager → Startup apps. Lost: "restart on failure" and the
   faster task start that matters only for the Face "Unlocked" animation (Phase 8, revisit then).
5. **Upgrades while running:** Restart Manager closes the Island; the Island calls `RegisterApplicationRestart`
   (no crash/hang/reboot restarts), so Windows starts it again after the upgrade. Measured: no reboot, Island back.
6. **Signing:** `scripts/build-release.ps1` signs IrisAura's own EXEs/DLLs and the MSI with SHA-256. Development
   releases use the development certificate (in `CurrentUser\My` only, never trusted machine-wide). Production
   releases require `-CertificateThumbprint` (hardware token/HSM) and `-TimestampUrl` (RFC 3161).
7. **Update verification, offline:** a JSON manifest (product, version, file, SHA-256, size) signed with ECDSA P-256 by
   a non-exportable CNG key (`IrisAura.Tools update-key/update-manifest`). The app accepts an update only if the
   manifest signature, product, newer version, size, hash and the MSI's Authenticode signer (known publisher or pinned)
   all check out (`--verify-update`). The development update key is trusted only in builds made with
   `TrustDevelopmentKeys=true`. **No download or update check is built**: it needs a release server and the owner's
   approval of a network call (KI-043).
8. **Lock files:** `RuntimeIdentifiers=win-x64` in `Directory.Build.props`, so build, test and publish restores write
   the same lock files.

## Consequences

- Evidence: `docs/evidence/release-1-install-test-2026-11-26.json` — 29/29 (install, update verification incl.
  tampered MSI and edited manifest, upgrade while running without reboot, uninstall).
- Release 1 is **final only after**: production certificate + timestamp (KI-039/040), independent review of the SDK
  pipe (KI-041), the OSMF payment arrangement (KI-047), and a release built on the latest .NET 10 servicing SDK (KI-044).
- Per-user data (`%LocalAppData%\IrisAura`) survives uninstall by design (it belongs to each user).

## Alternatives considered

| Option | Why not |
|---|---|
| WiX 6.0.2 | Older; 7.0.1 is current and over two weeks old |
| Visual Studio Installer Projects | Weaker upgrade and custom-action support; owner chose WiX |
| Framework-dependent app + Burn bundle installing .NET | Bigger moving parts; self-contained has no prerequisites |
| Scheduled task via schtasks custom action | Needs a custom action and XML rewriting for the install path; Run key is enough for Release 1 |
| Trust the development certificate machine-wide for tests | Trust-store change; pinning tests the same code |
