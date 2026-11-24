# Known Issues

Open issues, limitations and unverified assumptions. Close an item by linking the evidence
(PROGRESS entry, ADR or test). Never paste biometric or credential data here.

| ID | Area | Issue | Impact | Next step / owner | Status |
|---|---|---|---|---|---|
| KI-001 | Build | Native project not yet compiled: Visual Studio C++ tools and Windows SDK not installed on the dev machine. The `.vcxproj` is unverified. | Phase 0A acceptance incomplete | Install VS C++ workload + ARM64 tools; run `scripts/build.ps1` | Open |
| KI-002 | Environment | Dev machine is Windows 11 **Home**: no Hyper-V for the disposable VM. | Blocks Phase 0C VM work | Use VMware Workstation Pro / VirtualBox, or upgrade to Pro ([VM_SETUP.md](VM_SETUP.md)) | Open |
| KI-003 | Brand | Simplified icon mark and tray mark are **drafts**; the full-colour mark is muddy at 16 px. | Release icons | Owner approval; consider a hand-tuned 16 px variant from a designer | Open |
| KI-004 | Brand | `build-brand-assets.ps1` not yet run (ImageMagick not installed); SVG rendering quality in ImageMagick's built-in renderer is unverified. ⚠ VERIFY | Icon quality | Install ImageMagick 7, run, inspect outputs | Open |
| KI-005 | Installer | Default WiX UI draws **black** text over the banner/dialog bitmaps, which conflicts with the dark-background-only logo rule (BLUEPRINT §0.3, §0.6). | Installer art | Design with the installer UI in Phase 9 (ADR 0003) | Deferred |
| KI-006 | Brand | No vector (SVG) master of the full logo symbol. ⚠ VERIFY (BLUEPRINT §0.3) | High-DPI art quality | Obtain from the designer | Open |
| KI-007 | Legal | Logo provenance and trademark search not yet recorded. | Release blocker | Fill in [BRAND_LICENSE.md](BRAND_LICENSE.md) | Open |
| KI-008 | Hotkeys | Default hotkeys `Ctrl+Alt+Shift+I/T` not yet checked against common apps. ⚠ VERIFY (BLUEPRINT §8.3) | Possible conflicts | Test in Phase 1 | Open |
| KI-009 | Fonts | `Segoe UI Variable Text/Display` family names used for WPF; WPF's handling of variable-font instances is unverified. ⚠ VERIFY | Typography on Windows 11 | Check rendering in Phase 1; fallback is Segoe UI | Open |

## Unverified blueprint items (⚠ VERIFY) carried forward

Tracked in BLUEPRINT; listed here when work depends on them: pre-logon camera access (Phase 0B),
IR stream access with Windows Hello/ESS (Phase 0B), WM_HOTKEY foreground permission (Phase 1),
WPF 120 fps (Phase 1), `NotificationChanged` in desktop apps (Phase 4), Bluetooth battery properties
(Phase 4), volume-flyout hiding per build (Phase 4), per-app SMTC support (Phase 3).
