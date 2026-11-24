# Disposable VM Setup

Credential-provider experiments (Phase 0C, Phase 8) run **only** in a disposable Windows 11 VM with
checkpoints, never on the developer workstation (BLUEPRINT §10 golden rules).

> A VM usually cannot expose representative camera hardware. Camera experiments (Phase 0B) need a
> **spare physical device** with a second local administrator account and a known BitLocker recovery key.

## 1. Choose a hypervisor

The developer workstation runs **Windows 11 Home**, which has no Hyper-V. Options:

| Option | Cost | Notes |
|---|---|---|
| VMware Workstation Pro | Free for personal use (⚠ VERIFY current terms for commercial use) | Snapshots, virtual TPM — good fit |
| Oracle VirtualBox | Free (GPLv2 base) | Snapshots; Windows 11 needs TPM/Secure Boot settings |
| Upgrade to Windows 11 Pro → Hyper-V | Licence upgrade | Checkpoints; `Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All` |

## 2. Create the VM

1. Download the Windows 11 ISO from Microsoft (x64; ARM64 needs ARM hardware).
2. VM: 4 vCPU, 8 GB RAM, 80 GB disk, UEFI + Secure Boot, **virtual TPM** enabled.
3. Install Windows 11. Create **two local administrator accounts**: `labadmin` and `labrecovery`.
   Use strong, test-only passwords stored in a password manager — never real ones.
4. Note the Windows build (`winver`) in [HARDWARE_MATRIX.md](HARDWARE_MATRIX.md).
5. Leave BitLocker off in the VM (simpler WinRE recovery); record that choice.
6. Install the Visual C++ debugging tools only if needed; keep the VM minimal.

## 3. Checkpoints

| Name | When |
|---|---|
| `00-clean` | Right after Windows setup and updates, both admin accounts created |
| `01-tools` | After copying test tools (no IrisAura components registered) |
| `phase-0c-<step>` | Before each provider experiment |

## 4. Prove recovery (Phase 0A acceptance)

1. Make a harmless change (for example create `C:\checkpoint-test.txt`).
2. Restore `00-clean`.
3. Confirm the file is gone and both admin accounts can sign in.
4. Record the date, hypervisor and Windows build in [PROGRESS.md](PROGRESS.md).

## 5. Rules inside the VM

- Register a provider only from a script that can also disable it (`cp-register.ps1` / `cp-disable.ps1`, Phase 0C).
- Before every experiment: take a checkpoint, confirm `labrecovery` can sign in.
- Never copy real credentials, real face data or production certificates into the VM.
