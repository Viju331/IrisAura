<#
.SYNOPSIS
    Generates IrisAura icon and art sizes from assets/brand (BLUEPRINT §0.6).

.DESCRIPTION
    Reads the original logos and the source SVGs in assets/brand/source and writes everything to
    assets/brand/generated (git-ignored; re-run any time). The original PNGs are never modified.

    Requires ImageMagick 7 (`magick` on PATH). Build-time tool only; nothing from it is shipped.

    Generated:
      app.ico                 16/20/24/32/40/48 from the simplified mark, 64/256 from the full symbol
      tray.ico                16/20/24/32 monochrome bracket-person
      package/                sparse-package logos (Square44x44 + targetsize-*, Square150x150, StoreLogo)
      about.png, about@2x.png 512 / 1024 splash & About art (full symbol)
      readme-header.png       1280x640 wordmark on black

    Not generated yet: WiX banner/dialog BMPs. The default WiX UI draws black text over those
    bitmaps, which conflicts with the dark-only logo rule; they are designed together with the
    installer UI in Phase 9 (docs/KNOWN_ISSUES.md).

    Check generated icons at 100 %, 150 % and 200 % scaling on a real display.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$brand = Join-Path $root 'assets\brand'
$src = Join-Path $brand 'source'
$out = Join-Path $brand 'generated'
$tmp = Join-Path $out '_work'

if (-not (Get-Command magick -ErrorAction SilentlyContinue)) {
    throw 'ImageMagick 7 is required: install it and make sure `magick` is on PATH (https://imagemagick.org).'
}

function Invoke-Magick {
    & magick @args
    if ($LASTEXITCODE -ne 0) { throw "magick failed: $($args -join ' ')" }
}

$logo = Join-Path $brand 'irisaura-logo.png'          # 1536x1024, symbol on black
$wordmark = Join-Path $brand 'irisaura-wordmark.png'  # 1254x1254, symbol + text on black
$mark = Join-Path $src 'irisaura-mark.svg'            # simplified mark (draft, needs approval)
$trayMark = Join-Path $src 'irisaura-tray-mono.svg'

foreach ($f in $logo, $wordmark, $mark, $trayMark) {
    if (-not (Test-Path $f)) { throw "Missing input: $f" }
}

New-Item -ItemType Directory -Force -Path $out, $tmp, (Join-Path $out 'package') | Out-Null

# Full symbol cropped to a centred square (the logo is 1536x1024).
$symbolSquare = Join-Path $tmp 'symbol-square.png'
Invoke-Magick $logo -gravity center -crop 1024x1024+0+0 +repage $symbolSquare

function New-FromSvg([string] $svg, [int] $size, [string] $path) {
    # Rasterise at high density, then downscale, for crisp small sizes.
    Invoke-Magick -background none -density 1536 $svg -resize "${size}x${size}" $path
}

# App icon
$appPngs = @()
foreach ($s in 16, 20, 24, 32, 40, 48) {
    $p = Join-Path $tmp "app-$s.png"; New-FromSvg $mark $s $p; $appPngs += $p
}
foreach ($s in 64, 256) {
    $p = Join-Path $tmp "app-$s.png"; Invoke-Magick $symbolSquare -resize "${s}x${s}" $p; $appPngs += $p
}
Invoke-Magick @appPngs (Join-Path $out 'app.ico')

# Tray icon (monochrome)
$trayPngs = @()
foreach ($s in 16, 20, 24, 32) {
    $p = Join-Path $tmp "tray-$s.png"; New-FromSvg $trayMark $s $p; $trayPngs += $p
}
Invoke-Magick @trayPngs (Join-Path $out 'tray.ico')

# Sparse package logos (identity packages still need them; BLUEPRINT §8.6.9)
$pkg = Join-Path $out 'package'
New-FromSvg $mark 44 (Join-Path $pkg 'Square44x44Logo.png')
foreach ($s in 16, 24, 32, 48, 256) {
    New-FromSvg $mark $s (Join-Path $pkg "Square44x44Logo.targetsize-$s.png")
}
New-FromSvg $mark 150 (Join-Path $pkg 'Square150x150Logo.png')
New-FromSvg $mark 50 (Join-Path $pkg 'StoreLogo.png')

# About / splash art
Invoke-Magick $symbolSquare -resize 512x512 (Join-Path $out 'about.png')
Invoke-Magick $symbolSquare -resize 1024x1024 (Join-Path $out 'about@2x.png')

# README header: wordmark centred on black
Invoke-Magick -size 1280x640 xc:black '(' $wordmark -resize x620 ')' -gravity center -composite (Join-Path $out 'readme-header.png')

Remove-Item -Recurse -Force $tmp
Write-Host "Brand assets written to $out" -ForegroundColor Green
Get-ChildItem -Recurse $out -File | ForEach-Object { Write-Host ('  ' + $_.FullName.Substring($out.Length + 1)) }
