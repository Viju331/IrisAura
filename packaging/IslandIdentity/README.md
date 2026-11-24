# Island sparse identity package (Phase 4)

A tiny signed MSIX containing only `AppxManifest.xml` (identity name `IrisAura.Island`,
`uap10:AllowExternalContent`, `userNotificationListener` capability) that gives
`IrisAura.Island.exe` package identity for the notification listener (BLUEPRINT §8.6.9, §12.1).

- Publisher must equal the signing certificate subject exactly
  (development: `CN=IrisAura Development` from `scripts/new-dev-cert.ps1`).
- Logos come from `assets/brand/generated/package/` (`scripts/build-brand-assets.ps1`).
- Registered by the MSI with an external location; removed on uninstall.
