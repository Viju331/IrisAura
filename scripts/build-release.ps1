<#
.SYNOPSIS
    Builds a signed IrisAura Island release: self-contained app + CLI, Authenticode-signed binaries, per-machine MSI,
    SHA-256 sums, and a signed update manifest (BLUEPRINT §12, Release 1; ADR 0010).

.DESCRIPTION
    1. Publishes IrisAura.Island (self-contained win-x64, ReadyToRun) and irisaura-island (single file) into
       artifacts\release\<version>\publish.
    2. Signs IrisAura-built EXEs/DLLs (not third-party or runtime files) with the given certificate (SHA-256).
       -TimestampUrl adds an RFC 3161 timestamp (required for production; it is a network call to the CA).
    3. Builds the MSI (WiX, installer\IrisAura.Msi) and signs it.
    4. Writes SHA256SUMS, the update manifest (IrisAura.Tools update-manifest write) and its ECDSA signature
       (update-manifest sign), then verifies the result offline with IrisAura.Island.exe --verify-update.

    Development releases (-Development): signed with the development certificate (scripts/new-dev-cert.ps1, never
    trusted machine-wide) and the development update key, and built so the app also trusts that key. Production
    releases need -CertificateThumbprint of the production certificate (hardware token / HSM) and -TimestampUrl.

.EXAMPLE
    ./scripts/build-release.ps1 -Version 1.0.0 -Development
    ./scripts/build-release.ps1 -Version 1.0.0 -CertificateThumbprint <sha1> -TimestampUrl http://timestamp.example -UpdateKeyName "IrisAura Update Signing"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [switch] $Development,
    [string] $CertificateThumbprint,
    [string] $TimestampUrl,
    [string] $UpdateKeyName = 'IrisAura Update Signing (Development)'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\release\$Version"
$publish = Join-Path $out 'publish'
$signtool = (Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' | Sort-Object FullName -Descending | Select-Object -First 1).FullName
if (-not $signtool) { throw 'signtool.exe (Windows SDK) not found.' }

if ($Development) {
    & (Join-Path $PSScriptRoot 'new-dev-cert.ps1') | Out-Null
    $CertificateThumbprint = (Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=IrisAura Development' -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1).Thumbprint
}
if (-not $CertificateThumbprint) { throw 'Pass -Development or -CertificateThumbprint.' }
if (-not $Development -and -not $TimestampUrl) { throw 'Production releases need -TimestampUrl (RFC 3161).' }

function Invoke-Checked([string] $What, [scriptblock] $Command) {
    $ErrorActionPreference = 'Continue' # native tools write progress to stderr; the exit code decides
    $output = & $Command 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        $output | Select-Object -Last 30 | Write-Host
        throw "$What failed (exit $LASTEXITCODE)"
    }
}

function Sign([string[]] $Files) {
    $signArgs = @('sign', '/q', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, '/s', 'My')
    if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    Invoke-Checked 'signtool' { & $signtool @signArgs @Files }
}

Push-Location $root
try {
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $publish | Out-Null
    $common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', "-p:Version=$Version", '-v', 'm', '--nologo')
    $devTrust = @(if ($Development) { '-p:TrustDevelopmentKeys=true' })   # @(...) keeps it an array for splatting

    Write-Host "==> Publishing IrisAura Island $Version"
    Invoke-Checked 'publish Island' { dotnet publish src/IrisAura.Island @common @devTrust -p:PublishReadyToRun=true -o $publish }
    Invoke-Checked 'publish CLI' { dotnet publish src/IrisAura.Island.Cli @common -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $out 'cli') }
    Copy-Item (Join-Path $out 'cli\irisaura-island.exe') $publish
    Get-ChildItem $publish -Include *.pdb, *.xml -Recurse | Remove-Item -Force   # no symbols or doc XML in the installer

    Write-Host '==> Signing IrisAura binaries'
    $ours = Get-ChildItem $publish -File | Where-Object { $_.Name -like 'IrisAura.*' -or $_.Name -eq 'irisaura-island.exe' } |
        Where-Object { $_.Extension -in '.exe', '.dll' }
    Sign ($ours.FullName)

    Write-Host '==> Building the MSI'
    Invoke-Checked 'MSI build' { dotnet build installer/IrisAura.Msi -c Release --no-incremental "-p:ProductVersion=$Version" "-p:PublishDir=$publish\" -v q --nologo }
    $msiName = "IrisAura.Island-$Version-x64.msi"
    $msi = Join-Path $out $msiName
    Copy-Item (Join-Path $root "installer\IrisAura.Msi\bin\Release\$msiName") $msi
    Sign @($msi)

    Write-Host '==> Checksums and update manifest'
    $hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path (Join-Path $out 'SHA256SUMS') -Value "$hash  $msiName" -Encoding ascii
    Invoke-Checked 'Tools build' { dotnet build src/IrisAura.Tools -c Release -v q --nologo }
    $tools = Join-Path $root 'src\IrisAura.Tools\bin\Release\net10.0-windows10.0.19041.0\IrisAura.Tools.exe'
    $manifest = Join-Path $out 'update-manifest.json'
    Invoke-Checked 'manifest' { & $tools update-manifest write --msi $msi --version $Version --out $manifest | Out-Null }
    Invoke-Checked 'manifest signature' { & $tools update-manifest sign --manifest $manifest --name $UpdateKeyName | Out-Null }

    Write-Host '==> Verifying offline'
    $verifyArgs = @('--verify-update', $manifest, $msi)
    if ($Development) { $verifyArgs += @('--trust-thumbprint', $CertificateThumbprint) }
    $p = Start-Process (Join-Path $publish 'IrisAura.Island.exe') -ArgumentList $verifyArgs -Wait -PassThru
    $verdict = Get-Content "$manifest.verify.txt" -Raw
    Remove-Item "$manifest.verify.txt"
    # A fresh build of version V reports "not newer" against itself (V is not newer than V): signature, hash and signer passed first.
    if ($p.ExitCode -ne 0 -and $verdict -notmatch '^NotNewer') { throw "Update verification failed: $verdict" }

    $signature = Get-AuthenticodeSignature $msi
    [ordered]@{
        version = $Version
        development = [bool]$Development
        msi = $msiName
        msiBytes = (Get-Item $msi).Length
        sha256 = $hash
        signer = $signature.SignerCertificate.Subject
        signerThumbprint = $signature.SignerCertificate.Thumbprint
        signatureStatus = "$($signature.Status)"
        timestamped = [bool]$TimestampUrl
        signedBinaries = $ours.Count
        installedFiles = (Get-ChildItem $publish -File -Recurse).Count
        runtime = (Get-Content (Join-Path $publish 'IrisAura.Island.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks.version | Select-Object -First 1
        updateVerification = $verdict.Trim()
    } | ConvertTo-Json | Set-Content (Join-Path $out 'release.json') -Encoding utf8
    Write-Host "Release $Version ready in $out"
}
finally {
    Pop-Location
}
