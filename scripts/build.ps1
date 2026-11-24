<#
.SYNOPSIS
    Build validation for IrisAura: restore, build (warnings are errors) and test the C# projects,
    then build the native projects for x64 and ARM64 when Visual Studio C++ tools are installed.

.EXAMPLE
    ./scripts/build.ps1
    ./scripts/build.ps1 -Configuration Debug -SkipNative
    ./scripts/build.ps1 -Locked        # CI: fail if packages.lock.json files are out of date
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipNative,
    [switch] $SkipTests,
    [switch] $Locked
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = [ordered]@{}

function Invoke-Step([string] $name, [scriptblock] $action) {
    Write-Host "`n==> $name" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) {
        $results[$name] = 'FAILED'
        Write-Summary
        throw "$name failed with exit code $LASTEXITCODE."
    }
    $results[$name] = 'passed'
}

function Write-Summary {
    Write-Host "`nSummary ($Configuration)" -ForegroundColor Cyan
    foreach ($key in $results.Keys) { Write-Host ("  {0,-28} {1}" -f $key, $results[$key]) }
}

Push-Location $root
try {
    $ciArgs = @()
    if ($Locked) { $ciArgs = @('-p:ContinuousIntegrationBuild=true') }

    Invoke-Step 'C# build' { dotnet build IrisAura.Managed.slnf -c $Configuration -nologo @ciArgs }

    if ($SkipTests) {
        $results['C# tests'] = 'skipped'
    }
    else {
        Invoke-Step 'C# tests' { dotnet test --solution IrisAura.Managed.slnf -c $Configuration --no-build }
    }

    if ($SkipNative) {
        $results['Native build'] = 'skipped (-SkipNative)'
    }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        $msbuild = $null
        if (Test-Path $vswhere) {
            $msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        }

        if (-not $msbuild) {
            Write-Warning 'Visual Studio C++ tools not found; native projects were NOT built. Install the "Desktop development with C++" workload (MSVC x64 + ARM64, Windows 11 SDK).'
            $results['Native build'] = 'SKIPPED (MSVC not installed)'
        }
        else {
            $hasArm64 = [bool](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -property installationPath)
            $platforms = @('x64')
            if ($hasArm64) { $platforms += 'ARM64' }
            else { Write-Warning 'MSVC ARM64 tools not installed; ARM64 native build skipped.' }

            foreach ($platform in $platforms) {
                Invoke-Step "Native build ($platform)" {
                    & $msbuild 'native\IrisAura.Face.CredentialProvider\IrisAura.Face.CredentialProvider.vcxproj' `
                        "/p:Configuration=$Configuration" "/p:Platform=$platform" /m /nologo /v:minimal
                }
            }
            if (-not $hasArm64) { $results['Native build (ARM64)'] = 'SKIPPED (ARM64 tools not installed)' }
        }
    }

    Write-Summary
}
finally {
    Pop-Location
}
