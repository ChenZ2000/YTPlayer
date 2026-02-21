param(
    [Parameter(Mandatory = $true)]
    [string]$PngPath,

    [Parameter(Mandatory = $true)]
    [string]$IcoPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PngPath)) {
    throw "Icon source not found: $PngPath"
}

$outDir = Split-Path -Parent $IcoPath
if (-not [string]::IsNullOrWhiteSpace($outDir) -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class IconNativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
"@

$image = [System.Drawing.Image]::FromFile($PngPath)
$bitmap = $null
$graphics = $null
$icon = $null
$iconClone = $null
$stream = $null
$hIcon = [IntPtr]::Zero

try {
    $targetSize = 256
    $bitmap = New-Object System.Drawing.Bitmap($targetSize, $targetSize)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($image, 0, 0, $targetSize, $targetSize)

    $hIcon = $bitmap.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($hIcon)
    $iconClone = [System.Drawing.Icon]$icon.Clone()
    $stream = [System.IO.File]::Open($IcoPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $iconClone.Save($stream)
}
finally {
    if ($stream -ne $null) { $stream.Dispose() }
    if ($iconClone -ne $null) { $iconClone.Dispose() }
    if ($icon -ne $null) { $icon.Dispose() }
    if ($hIcon -ne [IntPtr]::Zero) { [IconNativeMethods]::DestroyIcon($hIcon) | Out-Null }
    if ($graphics -ne $null) { $graphics.Dispose() }
    if ($bitmap -ne $null) { $bitmap.Dispose() }
    if ($image -ne $null) { $image.Dispose() }
}
