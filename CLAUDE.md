# IrisAura — rules for Claude Code

- The source of truth is docs/BLUEPRINT.md. Work ONLY on the exact phase I name. Do not infer permission to proceed to later phases.
- Before coding: restate scope, acceptance criteria, assumptions, risks, and files to create/modify. Ask for approval if scope or a security boundary changes. Implement tests and evidence.
- NEVER register or enable the custom credential provider on the developer's primary machine. Provider work is restricted to a disposable VM or explicitly approved spare test device with a recovery plan.
- NEVER log passwords, face images, embeddings, biometric scores linked to a person, or notification contents. NEVER add input injection, keystroke simulation, undocumented API hooks, or security bypasses.
- No camera/ML/network code inside the C++ credential provider DLL. No exception may cross a COM boundary. Keep the provider minimal and fail closed.
- Ask before adding any NuGet package or model not listed in BLUEPRINT §4/§13. Pin every package version in Directory.Packages.props.
- Never use model weights without verifying the exact file-level commercial licence and redistribution rights. Never use private/undocumented Windows APIs (e.g. NPSM) in product code.
- Do not use Apple product names, logos, glyphs, SF Symbols, sounds, or copied trade dress in product UI or resources. Internal technical documentation may use comparative references when clearly descriptive.
- Product name is IrisAura. Use the exact names, namespaces, paths and pipe names in BLUEPRINT §0.2. Use only the brand colours/tokens in §0.4 and the logo files in assets/brand/. Never edit the original logo PNGs, and never invent a different logo. Never write "iris scan"/"iris recognition" in UI text.
- IrisAura.Island* projects must never reference IrisAura.Face.* projects (§6 Release 1 dependency rule). Shared brand resources live only in IrisAura.Theme.
- Global hotkeys must not use the Windows key. Never use HTTRANSPARENT for cross-app click-through (§8.3).
- Target: net10.0-windows10.0.19041.0, C# 14, Nullable enable, TreatWarningsAsErrors. C++20, x64 + ARM64. Pin SDK and package versions; update .NET 10 servicing patches.
- After each phase: run tests, capture reproducible evidence, update docs/PROGRESS.md, docs/KNOWN_ISSUES.md and docs/HARDWARE_MATRIX.md where relevant. Never report an untested feature as complete.
- Never claim biometric FAR/APCER/BPCER, spoof resistance, ESS compatibility, or camera support from mock tests or small convenience datasets.
- Phase 0B camera access and Phase 0C Credential Provider feasibility are mandatory gates. Do not bypass a failed gate.
- Do not store Windows passwords unless the user explicitly approves the isolated lab-vault experiment after reviewing its threat model. Never add password-vault code to IrisAura Island or IrisAura Face Lab.
- Prefer Windows Hello for production sign-in. The custom sign-in track is experimental and may be cancelled.
- Do not silently add telemetry, cloud processing, analytics, crash uploads, or network calls.
- Every third-party model, package, project, font, icon, and asset needs a recorded licence and redistribution review.

## Working in this repository

- Build and test: `./scripts/build.ps1` (or `dotnet build IrisAura.Managed.slnf` / `dotnet test IrisAura.Managed.slnf`). Native projects need Visual Studio C++ tools; the script skips them with a warning if MSVC is missing.
- Use `IrisAura.Managed.slnf` with the dotnet CLI; `IrisAura.sln` also contains the C++ project, which only MSBuild from Visual Studio can build.
- Owner decision (2026-11-24): implementation continues directly on `main`. Create a separate branch only to fix issues found after testing. Commit only after `./scripts/build.ps1` passes.
- After changing packages, run `./scripts/generate-dependency-inventory.ps1` and update THIRD_PARTY_NOTICES.md.
- Record architecture decisions as ADRs in docs/DECISIONS/ (copy docs/DECISIONS/0000-template.md).
