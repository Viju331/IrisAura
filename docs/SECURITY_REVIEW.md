# Security Review

Record of security reviews and gate sign-offs (BLUEPRINT §7.8, §9, §10). A gate passes only with a
named reviewer, a date and linked evidence. Independent review means someone other than the author.

## Gates

| Gate | Phase | Required before | Reviewer | Date | Result | Evidence |
|---|---|---|---|---|---|---|
| Pre-logon camera feasibility (A/B/C outcome) | 0B | Phase 8 | | | ⏳ | |
| Credential Provider feasibility + recovery | 0C | Phase 8 | | | ⏳ | |
| Account-type support matrix + credential lifecycle design | 0C | Phase 8 | | | ⏳ | |
| Island SDK pipe security (ACL, squatting, impersonation, rate limits) | 5 | Release 1 | | | ⏳ | |
| Presentation-attack evaluation methodology | 7 | Phase 8 | | | ⏳ | |
| Independent security review of sign-in prototype | 8 | Any external pilot | | | ⏳ | |
| Legal / privacy review | 8, 9 | Any external pilot / release | | | ⏳ | |
| Release security checklist (signing, SBOM, update verification) | 9 | Each release | | | ⏳ | |

## Phase checklists

### Phase 0A (repository) — self-review, 2026-11-24

- [x] No secrets, certificates or private keys in the repository (`.gitignore` blocks `*.pfx`, `*.p12`, `*.snk`, `*.cer`).
- [x] NuGet restores only from nuget.org (`nuget.config` clears machine feeds; source mapping).
- [x] All package versions pinned centrally; lock files enabled; `-Locked` build fails on drift.
- [x] Credential-provider project is inert: no COM classes, no `DllRegisterServer`, cannot be registered.
- [x] No network, telemetry or logging code added.
- [x] Biometric datasets path (`tests/datasets/`) ignored by git.
- [ ] Native hardening flags (CFG, CET, /W4 /WX, static CRT) verified in a real build — pending KI-001.

## Findings

| ID | Date | Component | Finding | Severity | Status |
|---|---|---|---|---|---|
| | | | | | |
