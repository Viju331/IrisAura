# Phase 0B — camera and pre-logon feasibility report

BLUEPRINT §3.6, §10 Phase 0B (GO/NO-GO). Reproduce with `IrisAura.Tools cameras --probe --label <context> --out <file>`
(add `--with-holder` for "camera in use", `--delay <s>` to lock the workstation or close the lid before the probe).
The tool opens each camera source in exclusive and shared-read-only mode, counts frames for 3 s and keeps only frame
size, format, timing and an aggregate brightness level. **No frame is stored.**

**Status (2026-11-26): open.** One device class tested in the user session only. Outcome **(A) cannot be proven** with
the hardware and contexts allowed so far; the proposed outcome is **(B) capture only in a user session** (ADR 0011,
owner decision pending).

## Scope agreed with the owner

- Tests on DEV-01 run in the owner's user session only. **LocalSystem / Session 0 and pre-logon (LogonUI) tests are
  not run on the primary machine**; they need a disposable VM or a spare test device with a recovery plan
  (CLAUDE.md), and a VM usually has no real camera access (USB pass-through changes the result).
- No credential provider, no service, no driver or policy change, no bypass of Windows privacy controls.

## Device classes

| Class (BLUEPRINT §11.2) | Device | Status |
|---|---|---|
| RGB-only laptop (AMD) | DEV-01: HP Laptop 15s-gr0xxx, Windows 11 26H2 build 26300.10075, x64; HP TrueVision HD Camera (USB 04F2:B6F1, front panel, built in), driver 10.0.26100.10068 | User session tested |
| RGB-only laptop (Intel) | — | Needed |
| Windows Hello IR laptop without ESS | — | Needed |
| Windows Hello IR laptop with ESS | — | Needed |
| External USB webcam | — | Needed |
| Snapdragon ARM64 laptop | — | Needed (do not infer ARM64 from x64) |

## Results on DEV-01 (`docs/evidence/phase-0b/`)

| Context | Stream | Exclusive | Shared read-only | Evidence |
|---|---|---|---|---|
| Normal user, unlocked, Session 2 | RGB (YUY2 640×360) | ✅ 82 frames in 3 s, first after 473 ms | ✅ 89 frames, first after 241 ms | `DEV-01-user.json` |
| Normal user, camera held exclusively by another process | RGB | ⚠ **initialises, then 0 frames** (no error) | ✅ 87 frames, first after 118 ms | `DEV-01-camera-in-use.json` |
| Elevated user | RGB | ⏳ owner check (UAC prompt not approved in this session) | ⏳ | — |
| Workstation locked | RGB | ⏳ owner check (`--delay 15`, then Win+L) | ⏳ | — |
| Sleep / resume, lid close / open | RGB | ⏳ owner check | ⏳ | — |
| Camera disconnect / reconnect | — | n/a (built-in camera) | — | — |
| LocalSystem / Session 0 | — | ⏳ test device only | — | — |
| Pre-logon (LogonUI) | — | ⏳ test device only | — | — |
| IR / Depth | — | **none present** | — | enumeration |

Other facts recorded: Windows Hello supported (PIN) but **no Windows Hello face sensor**; Enhanced Sign-in Security not
configured; camera privacy allowed (device, user, desktop apps); no camera policy; no virtual cameras.

**Tier:** T3 (RGB only). Per BLUEPRINT §1.3 an RGB-only device is a convenience/demo tier: face unlock on it would not
be a security factor even if pre-logon capture worked.

## Findings

1. **Exclusive mode does not fail when the camera is busy.** `MediaCapture` initialises in `ExclusiveControl` and the
   frame reader starts successfully, but no frames arrive while another process holds the camera. BLUEPRINT §7.1's
   "if exclusive access fails, retry in SharedReadOnly" must be implemented as **"no frame within a timeout (≈ 1 s) →
   retry in SharedReadOnly"**, not as an exception handler.
2. Shared read-only delivers frames even while another app owns the camera, at the owner's format (here YUY2 640×360).
3. Auto-exposure needs a few frames: the first exclusive frames are dark (mean level 11 → 128), matching §7.1's
   "wait about 3 frames".
4. The tool reports the input desktop ("Default" when unlocked); locked or secure-desktop runs report `null`, which
   identifies the locked-workstation context in the evidence.

## Proposed outcome

**(B) capture only in a user session** for the tested class, and — until devices of the other classes and a safe
pre-logon test setup exist — for the product: stop the custom pre-logon unlock track (BLUEPRINT §3.6 gate), continue
IrisAura Island and IrisAura Face Lab (in-session), and keep Windows Hello as the sign-in method. This is an
acceptable outcome, not a failure. It can be revisited if the owner provides test devices and a disposable test
setup for the LocalSystem / pre-logon experiments.

## Owner checks to complete this device's row

```powershell
$t = "src\IrisAura.Tools\bin\Release\net10.0-windows10.0.19041.0\IrisAura.Tools.exe"
# Elevated: run from an administrator PowerShell
& $t cameras --probe --label elevated --out docs\evidence\phase-0b\DEV-01-elevated.json
# Locked: start, then press Win+L within 15 s; unlock after ~30 s
& $t cameras --probe --delay 15 --label locked --out docs\evidence\phase-0b\DEV-01-locked.json
# Sleep/resume: run again right after waking the laptop
& $t cameras --probe --label after-resume --out docs\evidence\phase-0b\DEV-01-after-resume.json
```
