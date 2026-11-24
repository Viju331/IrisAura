# Installer (Phase 9)

WiX Toolset MSI + Burn bundle (BLUEPRINT §12). Not started.

- `IrisAura.Msi/` — Island-only, per-machine MSI. Never contains a Face service or credential provider.
- `IrisAura.Bundle/` — Burn bootstrapper.
- Any experimental credential-provider package is a **separate** installer, registered with `Disabled=1`.
- Licensing: WiX v6+ requires the Open Source Maintenance Fee for revenue-generating use — decide before Phase 9.
- Banner/dialog art is designed with the installer UI (docs/DECISIONS/0003, KNOWN_ISSUES KI-005).
