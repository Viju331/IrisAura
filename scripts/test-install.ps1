<#
.SYNOPSIS
    Install / upgrade / uninstall test of the IrisAura Island MSI on this machine (BLUEPRINT §10 Phase 5 Release 1 gate,
    §9 Phase 9 "clean install, upgrade, uninstall"; ADR 0010).

.DESCRIPTION
    Needs two releases from scripts/build-release.ps1 (e.g. 1.0.0 and 1.0.1). Each msiexec step asks for administrator
    approval (UAC); everything else runs as the current user. Steps:
      1. install OLD → files, signatures, Start menu, sign-in start, PATH, ARP entry; no Face component, service or
         credential provider; the installed Island starts and serves the SDK; offline update check of NEW passes and
         tampered files are rejected;
      2. upgrade to NEW while the Island runs → one ARP entry at NEW, files at NEW, Island running again
         (Restart Manager), no reboot needed;
      3. uninstall → everything removed except per-user data (%LocalAppData%\IrisAura).
    Writes a JSON report (no user data).

.EXAMPLE
    ./scripts/test-install.ps1 -Old 1.0.0 -New 1.0.1 -Report docs/evidence/release-1-install-test.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Old,
    [Parameter(Mandatory)] [string] $New,
    [string] $Report = 'install-test.json'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:ProgramFiles 'IrisAura\Island'
$exe = Join-Path $installDir 'IrisAura.Island.exe'
$cli = Join-Path $installDir 'irisaura-island.exe'
$devThumb = (Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=IrisAura Development' } | Select-Object -First 1).Thumbprint
$checks = [System.Collections.Generic.List[object]]::new()
$logs = Join-Path $root 'artifacts\release\install-logs'
New-Item -ItemType Directory -Force $logs | Out-Null

function Check([string] $Name, [bool] $Pass, [string] $Detail = '') {
    $checks.Add([pscustomobject]@{ check = $Name; pass = $Pass; detail = $Detail })
    Write-Host ("{0}  {1}  {2}" -f $(if ($Pass) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
}

function Msi([string] $Arguments, [string] $Log) {
    # One UAC prompt per call: the user approves the installer.
    $p = Start-Process msiexec.exe -ArgumentList "$Arguments /qn /norestart /l*v `"$(Join-Path $logs $Log)`"" -Verb RunAs -Wait -PassThru
    return $p.ExitCode
}

function Arp {
    Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq 'IrisAura Island' }
}

function Invoke-IslandCli([string[]] $CliArgs) {  # not "Cli": PowerShell's built-in alias cli (Clear-Item) would win
    $ErrorActionPreference = 'Continue'
    $out = & $cli @CliArgs 2>&1 | ForEach-Object { "$_" } | Out-String
    [pscustomobject]@{ exit = $LASTEXITCODE; output = $out.Trim() }
}

function Wait-Island([bool] $Running, [int] $Seconds = 20) {
    for ($i = 0; $i -lt $Seconds * 4; $i++) {
        $p = Get-Process IrisAura.Island -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
        if ([bool]$p -eq $Running) { return $p }
        Start-Sleep -Milliseconds 250
    }
    return Get-Process IrisAura.Island -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
}

function PathHasInstallDir {
    $machinePath = [Environment]::GetEnvironmentVariable('PATH', 'Machine')
    return ($machinePath -split ';' | Where-Object { $_.TrimEnd('\') -eq $installDir.TrimEnd('\') }).Count -gt 0
}

$oldMsi = Join-Path $root "artifacts\release\$Old\IrisAura.Island-$Old-x64.msi"
$newDir = Join-Path $root "artifacts\release\$New"
$newMsi = Join-Path $newDir "IrisAura.Island-$New-x64.msi"
if (-not (Test-Path $oldMsi) -or -not (Test-Path $newMsi)) { throw 'Build both releases with scripts/build-release.ps1 first.' }

# A development Island from the repository would hold the single-instance lock.
Get-Process IrisAura.Island -ErrorAction SilentlyContinue | ForEach-Object { & $_.Path --quit }
Start-Sleep -Seconds 2
$existing = Arp
if ($existing -and $existing.DisplayVersion -ne $Old) { throw "IrisAura Island $($existing.DisplayVersion) is installed; uninstall it first." }

# ---------------------------------------------------------------- 1. install OLD
if ($existing) {
    # Resume: OLD is already installed by an earlier run of this script (its install log is in artifacts/release/install-logs).
    $logText = Get-Content (Join-Path $logs "install-$Old.log") -Raw -ErrorAction SilentlyContinue
    Check 'install: msiexec succeeded' ($logText -match 'Installation success or error status: 0') "from the earlier run's install log"
}
else {
    $code = Msi "/i `"$oldMsi`"" "install-$Old.log"
    Check 'install: msiexec succeeded' ($code -eq 0) "exit $code"
}
$arp = Arp
Check 'install: one Programs and Features entry' (@($arp).Count -eq 1 -and $arp.DisplayVersion -eq $Old) "$(@($arp).Count) entries, version $($arp.DisplayVersion)"
Check 'install: files in Program Files' ((Test-Path $exe) -and (Test-Path $cli) -and (Test-Path (Join-Path $installDir 'THIRD_PARTY_NOTICES.txt')) -and (Test-Path (Join-Path $installDir 'licenses\Apache-2.0.txt'))) $installDir
$sig = Get-AuthenticodeSignature $exe
Check 'install: IrisAura.Island.exe signed (development certificate)' ($sig.SignerCertificate.Thumbprint -eq $devThumb) "$($sig.SignerCertificate.Subject)"
Check 'install: version' ((Get-Item $exe).VersionInfo.ProductVersion -like "$Old*") (Get-Item $exe).VersionInfo.ProductVersion
$run = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).'IrisAura Island'
Check 'install: starts at sign-in (HKLM Run)' ($run -eq "`"$exe`"") "$run"
Check 'install: Start menu shortcut' (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\IrisAura Island.lnk'))
Check 'install: CLI folder on the system PATH' (PathHasInstallDir)
$face = Get-ChildItem $installDir -Recurse -Filter 'IrisAura.Face*' -ErrorAction SilentlyContinue
$services = Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'IrisAura*' }
$providers = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers' -ErrorAction SilentlyContinue |
    Where-Object { (Get-ItemProperty $_.PSPath).'(default)' -like '*IrisAura*' }
Check 'install: Island only (no Face files, services or credential provider)' (-not $face -and -not $services -and -not $providers) "face files $(@($face).Count), services $(@($services).Count), providers $(@($providers).Count)"

# Start the installed Island as the user and use its SDK.
Start-Process $exe | Out-Null
$island = Wait-Island $true
Check 'install: installed Island starts from Program Files' ([bool]$island) "pid $($island.Id)"
Start-Sleep -Seconds 3
$push = Invoke-IslandCli @('push', '--app', 'Install Test', '--id', 'install', '--title', 'Installed', '--trust-thumbprint', $devThumb)
Check 'install: SDK works from the installed CLI (pinned dev certificate)' ($push.exit -eq 0) $push.output
$logDir = Join-Path $env:LOCALAPPDATA 'IrisAura\logs'
$firstFrame = Get-ChildItem $logDir -Filter 'island-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    Get-Content | Select-String 'first frame (\d+) ms' | Select-Object -Last 1
Check 'install: startup to first frame (info, ReadyToRun)' $true "$($firstFrame.Matches[0].Groups[1].Value) ms"

# Offline update verification by the installed OLD app.
$manifest = Join-Path $newDir 'update-manifest.json'
function Verify([string] $Manifest, [string] $Msi) {
    $p = Start-Process $exe -ArgumentList @('--verify-update', $Manifest, $Msi, '--trust-thumbprint', $devThumb) -Wait -PassThru
    $text = Get-Content "$Manifest.verify.txt" -Raw
    Remove-Item "$Manifest.verify.txt"
    return [pscustomobject]@{ exit = $p.ExitCode; text = $text.Trim() }
}
$v = Verify $manifest $newMsi
Check "update: $New accepted by the installed $Old" ($v.exit -eq 0) $v.text
$tamperDir = Join-Path $env:TEMP 'irisaura-tamper'
New-Item -ItemType Directory -Force $tamperDir | Out-Null
$badMsi = Join-Path $tamperDir (Split-Path $newMsi -Leaf)
Copy-Item $newMsi $badMsi -Force
$bytes = [IO.File]::ReadAllBytes($badMsi); $bytes[$bytes.Length - 100] = $bytes[$bytes.Length - 100] -bxor 0xFF; [IO.File]::WriteAllBytes($badMsi, $bytes)
$v = Verify $manifest $badMsi
Check 'update: tampered installer rejected' ($v.exit -eq 2 -and $v.text -like 'HashMismatch*') $v.text
$badManifest = Join-Path $tamperDir 'update-manifest.json'
(Get-Content $manifest -Raw) -replace '"1\.0\.\d+"', '"9.9.9"' | Set-Content $badManifest -NoNewline
Copy-Item "$manifest.sig" "$badManifest.sig"
$v = Verify $badManifest $newMsi
Check 'update: edited manifest rejected' ($v.exit -eq 2 -and $v.text -like 'BadManifestSignature*') $v.text
Remove-Item -Recurse -Force $tamperDir

# ---------------------------------------------------------------- 2. upgrade to NEW while running
$code = Msi "/i `"$newMsi`"" "upgrade-$New.log"
Check 'upgrade: msiexec succeeded without a reboot' ($code -eq 0) "exit $code (3010 would mean a reboot is needed)"
$arp = Arp
Check 'upgrade: one entry, new version' (@($arp).Count -eq 1 -and $arp.DisplayVersion -eq $New) "$(@($arp).Count) entries, version $($arp.DisplayVersion)"
Check 'upgrade: files updated' ((Get-Item $exe).VersionInfo.ProductVersion -like "$New*") (Get-Item $exe).VersionInfo.ProductVersion
$island = Wait-Island $true 30
Check 'upgrade: Island running again (Restart Manager)' ([bool]$island) "pid $($island.Id)"
Start-Sleep -Seconds 2
$list = Invoke-IslandCli @('list', '--app', 'Install Test', '--trust-thumbprint', $devThumb)
Check 'upgrade: SDK answers after the upgrade' ($list.exit -eq 0) $list.output

# ---------------------------------------------------------------- 3. uninstall
$code = Msi "/x `"$newMsi`"" "uninstall-$New.log"
Check 'uninstall: msiexec succeeded' ($code -eq 0) "exit $code"
Start-Sleep -Seconds 1
Check 'uninstall: Island closed' (-not (Wait-Island $false 10))
Check 'uninstall: no Programs and Features entry' (-not (Arp))
Check 'uninstall: program folder removed' (-not (Test-Path (Join-Path $env:ProgramFiles 'IrisAura')))
Check 'uninstall: sign-in start removed' ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).'IrisAura Island')
Check 'uninstall: Start menu shortcut removed' (-not (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\IrisAura Island.lnk')))
Check 'uninstall: PATH entry removed' (-not (PathHasInstallDir))
Check 'uninstall: HKLM\Software\IrisAura removed' (-not (Test-Path 'HKLM:\SOFTWARE\IrisAura\Island'))
Check 'uninstall: per-user data kept (by design)' (Test-Path (Join-Path $env:LOCALAPPDATA 'IrisAura')) '%LocalAppData%\IrisAura'

[ordered]@{
    tool = 'scripts/test-install.ps1'
    timestamp = (Get-Date).ToString('o')
    windowsBuild = [Environment]::OSVersion.Version.ToString()
    old = $Old
    new = $New
    passed = @($checks | Where-Object pass).Count
    failed = @($checks | Where-Object { -not $_.pass }).Count
    checks = $checks
} | ConvertTo-Json -Depth 4 | Set-Content $Report -Encoding utf8
if ($checks | Where-Object { -not $_.pass }) { exit 1 }
