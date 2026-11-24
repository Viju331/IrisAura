# Threat Model

Living document. Starts from BLUEPRINT §9.1 and is refined per phase. Each threat lists where it is
mitigated and how the mitigation is tested. Status: ⏳ planned · 🔨 implemented · ✅ tested.

## Assets

| Asset | Where | Sensitivity |
|---|---|---|
| Face templates / embeddings | Face Lab store (Phase 6) | Special-category biometric data; never logged |
| Windows credentials | Not stored by default (BLUEPRINT §7.8) | Critical |
| Notification content | Island in-memory queue only | Personal data; never persisted or sent |
| Island SDK channel | Per-user named pipe | Integrity of what the Island shows |
| Signing keys | HSM / hardware token (never in repo) | Critical |
| Users' ability to sign in to Windows | Built-in providers | Must never be degraded |

## Trust boundaries

1. Interactive user session ↔ other local processes (Island SDK pipe).
2. User session ↔ LocalSystem services (Face pipes; only if Phase 0B/0C pass).
3. LogonUI / secure desktop ↔ credential provider DLL (conditional track).
4. Build/supply chain ↔ third-party packages and models.

## Threats

| ID | Threat | Boundary | Mitigation | Test | Status |
|---|---|---|---|---|---|
| T-01 | Rogue app spams or spoofs Island activities | 1 | Per-user pipe ACL, rate limits, priority cap 60, per-app block list, text sanitisation, 64 KB limit, protocol version check | Phase 5 malformed/oversized/rate-limit tests | ⏳ |
| T-02 | Pipe squatting: malicious process creates the SDK pipe first | 1 | Island creates pipe with `FILE_FLAG_FIRST_PIPE_INSTANCE`; SDK verifies server PID → signed `IrisAura.Island.exe` in caller's session | Phase 5 squatting test | ⏳ |
| T-03 | Pipe-name injection via crafted SID | 1 | `PipeNames.IslandActivities` accepts only digits/dashes in SID form | `PipeNamesTests` | ✅ |
| T-04 | Notification content leaks | 1 | "Hide content" option; no persistence; no network | Phase 4 tests | ⏳ |
| T-05 | Island depends on Face code (scope creep into Release 1) | — | Architecture rule | `ArchitectureTests` | ✅ |
| T-06 | Photo/screen replay (RGB) | 2 | Honest tiers; liveness research only; never an auth factor | Phase 7 evaluation | ⏳ |
| T-07 | Virtual/injected camera | 2 | Camera pinning (ID + VID/PID); reject virtual cameras | Phase 0B/6 | ⏳ |
| T-08 | Rogue process impersonates broker or provider | 2, 3 | Separate pipes, strict DACLs, token/SID/session validation; PID/path checks supplementary | Phase 8 | ⏳ |
| T-09 | User locked out by provider bug | 3 | Never filter providers; disabled by default; tested out-of-band recovery | Phase 0C recovery drill | ⏳ |
| T-10 | Credential theft | 2, 3 | No password vault by default; any lab vault needs separate threat model + review | Phase 8 gate | ⏳ |
| T-11 | Malicious or substituted NuGet package | 4 | nuget.org only + source mapping; pinned versions; lock files; `-Locked` CI restore | `build.ps1 -Locked` | 🔨 |
| T-12 | Model file tampering / licence violation | 4 | SHA-256 in `models/SHA256SUMS`, licences in `models/LICENSES/`, CI hash check | Phase 6 | ⏳ |
| T-13 | DLL hijack / tampering of installed files | — | Install to `%ProgramFiles%`, sign everything, `SetDefaultDllDirectories` | Phase 9 | ⏳ |
| T-14 | AV/EDR quarantine | — | Signing, no packers, no input injection | Phase 9 | ⏳ |
