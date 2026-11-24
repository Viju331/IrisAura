# 0002 — Build and test infrastructure

- **Status:** accepted
- **Date:** 2026-11-24
- **Phase:** 0A
- **Blueprint sections:** §4, §6, §17.1

## Context

Phase 0A needs a reproducible build for C# (.NET 10) and C++ (x64 + ARM64) projects, pinned
dependencies, and tests. Facts found while setting it up:

1. The dotnet CLI cannot load `.vcxproj` files without Visual Studio (`dotnet sln add` fails), so a
   solution containing the native project cannot be built with `dotnet build` alone.
2. MSBuild imports `Directory.Build.props` for `.vcxproj` files too, **before** `Microsoft.Cpp.props`.
   C++ item definitions placed there would be overwritten by the C++ defaults.
3. Current xunit (`xunit.v3` 4.x) runs on Microsoft.Testing.Platform. The .NET 10 SDK refuses the
   old VSTest path for it unless `global.json` opts into the new runner.
4. The developer machine has an extra local NuGet feed configured (`D:\Guts\Guts-Packages`).
5. FluentAssertions 8+ is commercially licensed.

## Decision

- `IrisAura.sln` holds every project (open it in Visual Studio). `IrisAura.Managed.slnf` holds only
  the C# projects and is what the dotnet CLI and `scripts/build.ps1` use. The script builds the
  native project with Visual Studio's MSBuild when MSVC is installed, and reports a skip otherwise.
- Solution platforms: `Any CPU` (C# only), `x64` and `ARM64` (C# + native). No x86.
- Root `Directory.Build.props` applies only to `.csproj`. C++ settings live in
  `native/IrisAura.Native.props`, imported by each `.vcxproj` right after `Microsoft.Cpp.props`.
  Static CRT, `/W4 /WX`, C++20, CFG and CET (x64) are set there.
- `global.json` selects a .NET 10 SDK and Microsoft.Testing.Platform. Originally pinned to 10.0.401
  (latest patch); **amended 2026-11-24 (Phase 1):** installing Visual Studio 2026 removed 10.0.401 and
  added the .NET 11 SDK, so `global.json` now requires ≥ 10.0.300 with `latestFeature` roll-forward —
  the newest installed 10.0.x SDK, never .NET 11 (KNOWN_ISSUES KI-016).
  Tests use `xunit.v3`; `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are not needed.
- Central package management (`Directory.Packages.props`) pins every approved package; lock files
  are on; `build.ps1 -Locked` restores in locked mode.
- `nuget.config` clears machine feeds and maps every package to nuget.org.
- FluentAssertions stays on 7.2.2 (Apache-2.0).

## Consequences

- Building native code needs Visual Studio's MSBuild, not just the .NET SDK. CI will need a Windows
  runner with the C++ workload.
- Contributors must use the slnf (or `build.ps1`) with the dotnet CLI.
- Upgrading xunit or the SDK requires checking the Microsoft.Testing.Platform configuration.

## Alternatives considered

| Option | Why not |
|---|---|
| Separate native `.sln` | Two solutions to keep in sync; VS users lose the single view |
| `native/Directory.Build.props` for C++ settings | Evaluated before C++ defaults, which override it |
| xunit v2 + VSTest | v2 is in maintenance; the .NET 10 SDK is moving to Microsoft.Testing.Platform |
| Keep machine NuGet feeds | Non-reproducible restores and a supply-chain risk |
