<#
.SYNOPSIS
    Creates a self-signed DEVELOPMENT code-signing certificate for IrisAura (BLUEPRINT §8.6.9, §12.4).

.DESCRIPTION
    Used to sign the sparse identity package and test builds on TEST MACHINES ONLY.
    Never ship anything signed with it, and never use it for a release.

    - Creates the certificate (RSA 3072, SHA-256, 2 years) in Cert:\CurrentUser\My.
      The private key stays in the current user's certificate store; nothing secret is written to disk.
    - Exports only the PUBLIC certificate to artifacts\certs\ (git-ignored).
    - With -TrustOnThisMachine (needs an elevated prompt), imports the public certificate into
      LocalMachine\TrustedPeople, which is what sideloading a dev-signed MSIX/sparse package needs.
      It is deliberately NOT added to Trusted Root.

    The sparse package manifest's Publisher must equal -Subject exactly.

.EXAMPLE
    ./scripts/new-dev-cert.ps1
    ./scripts/new-dev-cert.ps1 -TrustOnThisMachine      # from an elevated PowerShell on a test machine

.NOTES
    Remove later with:  Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\TrustedPeople |
                        Where-Object Subject -eq 'CN=IrisAura Development' | Remove-Item
#>
[CmdletBinding()]
param(
    [string] $Subject = 'CN=IrisAura Development',
    [switch] $TrustOnThisMachine
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'artifacts\certs'

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if ($existing) {
    Write-Host "Reusing existing development certificate $($existing.Thumbprint) (expires $($existing.NotAfter:d))."
    $cert = $existing
}
else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -FriendlyName 'IrisAura development code signing (NOT FOR RELEASE)' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @('2.5.29.19={text}')   # basic constraints: not a CA
    Write-Host "Created development certificate $($cert.Thumbprint)."
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$cerPath = Join-Path $outDir 'IrisAura.Development.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
Write-Host "Public certificate exported to $cerPath"

if ($TrustOnThisMachine) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw '-TrustOnThisMachine needs an elevated (Run as administrator) PowerShell.' }
    Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Write-Host 'Imported into LocalMachine\TrustedPeople (test machine only).'
}

Write-Host "`nSign with:  signtool sign /fd SHA256 /sha1 $($cert.Thumbprint) /s My <file>"
Write-Host "Manifest Publisher must be exactly:  $Subject"
