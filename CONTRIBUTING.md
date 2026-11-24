# Contributing

IrisAura is proprietary software (see [LICENSE](LICENSE)). External code contributions are not
accepted at this time. Bug reports are welcome through GitHub issues; security issues go through
[SECURITY.md](SECURITY.md).

## Rules for maintainers (and Claude Code)

The source of truth is [docs/BLUEPRINT.md](docs/BLUEPRINT.md); working rules are in
[CLAUDE.md](CLAUDE.md). In short:

- Work on one approved phase at a time, on a branch (`phase-<id>`), and merge when its acceptance
  evidence is recorded in [docs/PROGRESS.md](docs/PROGRESS.md).
- `./scripts/build.ps1` must pass: warnings are errors, and all tests pass.
- Pin every NuGet package in `Directory.Packages.props`; only packages approved in BLUEPRINT §4 may
  be added. Run `./scripts/generate-dependency-inventory.ps1` after any package change.
- Record any decision that changes the architecture as an ADR in [docs/DECISIONS/](docs/DECISIONS/).
- `IrisAura.Island*` projects must never reference `IrisAura.Face.*` projects (an architecture test
  enforces this).
- Never commit biometric data, credentials, certificates or private keys.
- Commit messages: imperative mood, short subject line, body explains why.
