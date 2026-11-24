# IrisAura.Face.CredentialProvider — inert skeleton

**Status: Phase 0A skeleton. Not a credential provider.**

This project builds a DLL with no COM classes and no registration entry points, for x64 and ARM64.
It only proves that the native toolchain works. It cannot be registered with Windows.

Rules (BLUEPRINT §3, §7.9, §9.2, §10):

- Real provider code is written **only after the Phase 0C gate**, starting from Microsoft's
  `Windows-classic-samples/Samples/CredentialProvider` sample.
- Register and test it **only in a disposable VM** with checkpoints, never on a primary machine.
- No camera, ML, network or UI framework code in this DLL. No exception may cross a COM boundary.
  Every failure path fails closed and leaves the built-in Windows providers available.
- Never filter, hide or disable other credential providers.
- Build both x64 and ARM64; ARM64 LogonUI silently skips an x64 DLL.
- Static CRT (`/MT`), so the DLL does not depend on the VC++ redistributable.

Build (needs Visual Studio with the C++ desktop workload, x64 + ARM64 tools, Windows 11 SDK):

```powershell
./scripts/build.ps1            # builds native too when MSVC is found
```

Output: `artifacts/native/<Platform>/<Configuration>/IrisAura.Face.CredentialProvider.dll`.
