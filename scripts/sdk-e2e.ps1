<#
.SYNOPSIS
    End-to-end check of the IrisAura Island Activity SDK against a running, development-signed Island (BLUEPRINT §10 Phase 5).

.DESCRIPTION
    1. Uses (or creates) the development certificate from scripts/new-dev-cert.ps1 in Cert:\CurrentUser\My.
       Nothing is added to any trusted store; the SDK trusts it only because its thumbprint is pinned.
    2. Signs the Release IrisAura.Island.exe with it (SHA-256, no timestamp: no network).
    3. Starts that Island and runs the irisaura-island command line:
         - pinned thumbprint: push / update / list / end must succeed;
         - another app name must not be able to end the activity (exit 4);
         - default verification (no pinned thumbprint) must refuse the dev-signed Island (exit 3);
         - --allow-unsigned-island must still refuse a server that is not IrisAura.Island.exe (covered by unit tests).
    4. Rebuilds the Island unsigned and checks the default verification refuses it (exit 3) and
       --allow-unsigned-island accepts it.
    Writes a JSON report (exit codes and messages only; no user data).

.EXAMPLE
    ./scripts/sdk-e2e.ps1 -Report docs/evidence/phase-5-sdk-e2e.json
#>
[CmdletBinding()]
param([string] $Report = 'sdk-e2e.json')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'src\IrisAura.Island\bin\Release\net10.0-windows10.0.19041.0'
$island = Join-Path $bin 'IrisAura.Island.exe'
$cli = Join-Path $root 'src\IrisAura.Island.Cli\bin\Release\net10.0-windows10.0.19041.0\irisaura-island.exe'
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe (Windows SDK) not found.' }

function Invoke-Cli([string[]] $CliArgs) {
    $ErrorActionPreference = 'Continue' # the CLI writes refusals to stderr; that is an expected result here
    $out = & $cli @CliArgs 2>&1 | ForEach-Object { "$_" } | Out-String
    [pscustomobject]@{ args = ($CliArgs -join ' '); exit = $LASTEXITCODE; output = $out.Trim() }
}

function Stop-Island {
    & $island --quit
    for ($i = 0; $i -lt 40 -and (Get-Process IrisAura.Island -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
}

function Start-Island {
    Start-Process $island | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ((Invoke-Cli @('list', '--allow-unsigned-island', '--app', 'e2e-probe')).exit -ne 2) { return }
    }
    throw 'IrisAura Island did not start its SDK pipe.'
}

Push-Location $root
try {
    dotnet build src/IrisAura.Island -c Release -v q | Out-Null
    dotnet build src/IrisAura.Island.Cli -c Release -v q | Out-Null

    & (Join-Path $PSScriptRoot 'new-dev-cert.ps1') | Out-Null
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=IrisAura Development' -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    $thumb = $cert.Thumbprint

    $results = [ordered]@{}
    Stop-Island

    # ---- development-signed Island
    & $signtool.FullName sign /q /fd SHA256 /sha1 $thumb /s My $island | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'signing failed' }
    $results.signature = (Get-AuthenticodeSignature $island | Select-Object @{n = 'status'; e = { "$($_.Status)" } }, @{n = 'signer'; e = { $_.SignerCertificate.Subject } }, @{n = 'thumbprint'; e = { $_.SignerCertificate.Thumbprint } })
    Start-Island
    $app = @('--app', 'E2E Test', '--trust-thumbprint', $thumb)
    $results.pinnedPush = Invoke-Cli (@('push', '--id', 'e2e', '--title', 'SDK end-to-end', '--progress', '0.25', '--text', '25%', '--button', 'stop=Stop') + $app)
    $results.pinnedUpdate = Invoke-Cli (@('update', '--id', 'e2e', '--progress', '0.75', '--text', '75%') + $app)
    $results.pinnedList = Invoke-Cli (@('list') + $app)
    $results.otherAppCannotEnd = Invoke-Cli @('end', '--id', 'e2e', '--app', 'Someone Else', '--trust-thumbprint', $thumb)
    $results.badLinkRefused = Invoke-Cli (@('push', '--id', 'link', '--title', 'x', '--open-uri', 'file:///C:/Windows/System32/calc.exe') + $app)
    $results.defaultRefusesDevSigned = Invoke-Cli @('list', '--app', 'E2E Test')
    $results.pinnedEnd = Invoke-Cli (@('end', '--id', 'e2e', '--final-text', 'Done') + $app)
    $results.listAfterEnd = Invoke-Cli (@('list') + $app)
    Stop-Island

    # ---- unsigned Island
    dotnet build src/IrisAura.Island -c Release -v q --no-incremental | Out-Null
    $results.unsignedSignature = "$((Get-AuthenticodeSignature $island).Status)"
    Start-Island
    $results.defaultRefusesUnsigned = Invoke-Cli @('list', '--app', 'E2E Test')
    $results.devFlagAcceptsUnsigned = Invoke-Cli @('list', '--app', 'E2E Test', '--allow-unsigned-island')
    Stop-Island

    $expected = [ordered]@{
        pinnedPush = 0; pinnedUpdate = 0; pinnedList = 0; otherAppCannotEnd = 4; badLinkRefused = 4
        defaultRefusesDevSigned = 3; pinnedEnd = 0; listAfterEnd = 0; defaultRefusesUnsigned = 3; devFlagAcceptsUnsigned = 0
    }
    $checks = foreach ($k in $expected.Keys) {
        [pscustomobject]@{ check = $k; expectedExit = $expected[$k]; actualExit = $results[$k].exit; pass = ($results[$k].exit -eq $expected[$k]) }
    }

    $reportObject = [ordered]@{
        tool = 'scripts/sdk-e2e.ps1'
        timestamp = (Get-Date).ToString('o')
        windowsBuild = [Environment]::OSVersion.Version.ToString()
        devCertificateInTrustedStores = [bool](Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPeople, Cert:\CurrentUser\Root, Cert:\CurrentUser\TrustedPeople |
            Where-Object Thumbprint -eq $thumb)
        checks = $checks
        results = $results
    }
    $reportObject | ConvertTo-Json -Depth 6 | Set-Content -Path $Report -Encoding utf8
    $checks | Format-Table -AutoSize | Out-String | Write-Host
    if ($checks | Where-Object { -not $_.pass }) { exit 1 }
}
finally {
    Pop-Location
}
