# Installer

`IrisAura.Msi/` — IrisAura Island per-machine MSI (WiX 7, Release 1; BLUEPRINT §12.1, ADR 0010). Island only: it
never contains a Face service, Face files or a credential provider. Any experimental credential-provider package will
be a **separate** installer registered with `Disabled=1`.

Build a release (publishes, signs, builds the MSI, writes SHA256SUMS and the signed update manifest):

```powershell
./scripts/build-release.ps1 -Version 1.0.0 -Development          # development certificate + development update key
./scripts/build-release.ps1 -Version 1.0.0 -CertificateThumbprint <sha1> -TimestampUrl <rfc3161-url> -UpdateKeyName "IrisAura Update Signing"
./scripts/test-install.ps1 -Old 1.0.0 -New 1.0.1                 # install / upgrade / uninstall test (UAC prompts)
```

- WiX licensing: the OSMF EULA is accepted in the project on the owner's decision; pay the Open Source Maintenance
  Fee for revenue-generating use (KI-047).
- `licenses/` holds the licence texts installed next to the app.
- No Burn bundle: the app is self-contained, so there are no prerequisites.
- Banner/dialog art: the MSI installs silently or with the default progress UI; designed dialogs come with Phase 9
  (KI-005).
