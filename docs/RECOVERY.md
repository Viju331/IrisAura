# Recovery

How to get back into Windows if an experimental IrisAura sign-in component misbehaves
(BLUEPRINT §9.2). **Every procedure here must be tested on each supported Windows build before any
release that contains a credential provider.**

## Current state (Phase 0A)

No IrisAura component is registered with Windows sign-in. The native project is an inert DLL with
no COM classes and no registration exports. **Nothing to recover from yet.**

IrisAura Island and IrisAura Face Lab never touch Windows sign-in; uninstalling them through
*Settings → Apps* is enough.

## Ground rules (apply from Phase 0C)

1. The custom provider never hides, filters or disables other providers. Password, PIN and Windows Hello stay available.
2. Experimental installers leave the provider **disabled**.
3. Provider experiments run only in a disposable VM with a checkpoint ([VM_SETUP.md](VM_SETUP.md)); camera tests on a spare device with a second local administrator account.
4. Recovery must never depend on the provider or its service working.

## Procedures to write and validate in Phase 0C

| Situation | Procedure | Validated on (build / date) |
|---|---|---|
| Signed in, provider misbehaves | `scripts/cp-disable.ps1` (elevated) — sets `Disabled=1` on the exact IrisAura CLSID only | ⏳ |
| Cannot sign in, other account available | Sign in as the second admin, run `cp-disable.ps1` | ⏳ |
| Cannot sign in at all | Safe Mode → sign in → `cp-disable.ps1` (⚠ VERIFY which providers Safe Mode offers on each build) | ⏳ |
| Safe Mode unusable | WinRE → Command Prompt → `reg load` the SOFTWARE hive → set `Disabled=1` for the IrisAura CLSID → `reg unload` | ⏳ |
| VM experiment broke the VM | Restore the "clean" checkpoint | ⏳ |

BitLocker: WinRE registry recovery needs the **BitLocker recovery key**. Record where it is kept
before any provider test on a physical device.
