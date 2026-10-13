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

$image = [System.Drawing.Image]::FromFile($PngPath)
$stream = $null
$writer = $null

function New-IconBitmap {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Image]$Source,

        [Parameter(Mandatory = $true)]
        [int]$Size
    )

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

        $scale = [Math]::Min($Size / [double]$Source.Width, $Size / [double]$Source.Height)
        $drawWidth = [int][Math]::Round($Source.Width * $scale)
        $drawHeight = [int][Math]::Round($Source.Height * $scale)
        $left = [int][Math]::Floor(($Size - $drawWidth) / 2)
        $top = [int][Math]::Floor(($Size - $drawHeight) / 2)
        $graphics.DrawImage($Source, $left, $top, $drawWidth, $drawHeight)
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Convert-BitmapToPngBytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap
    )

    $memory = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        return $memory.ToArray()
    }
    finally {
        $memory.Dispose()
    }
}

function Convert-BitmapToIconDibBytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap
    )

    $width = $Bitmap.Width
    $height = $Bitmap.Height
    $xorStride = $width * 4
    $andStride = [int]([Math]::Ceiling($width / 32.0) * 4)
    $memory = New-Object System.IO.MemoryStream
    $binaryWriter = New-Object System.IO.BinaryWriter($memory)

    try {
        $binaryWriter.Write([UInt32]40)
        $binaryWriter.Write([Int32]$width)
        $binaryWriter.Write([Int32]($height * 2))
        $binaryWriter.Write([UInt16]1)
        $binaryWriter.Write([UInt16]32)
        $binaryWriter.Write([UInt32]0)
        $binaryWriter.Write([UInt32]($xorStride * $height))
        $binaryWriter.Write([Int32]0)
        $binaryWriter.Write([Int32]0)
        $binaryWriter.Write([UInt32]0)
        $binaryWriter.Write([UInt32]0)

        for ($y = $height - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $width; $x++) {
                $pixel = $Bitmap.GetPixel($x, $y)
                $binaryWriter.Write([byte]$pixel.B)
                $binaryWriter.Write([byte]$pixel.G)
                $binaryWriter.Write([byte]$pixel.R)
                $binaryWriter.Write([byte]$pixel.A)
            }
        }

        $maskRow = New-Object byte[] $andStride
        for ($y = $height - 1; $y -ge 0; $y--) {
            [Array]::Clear($maskRow, 0, $maskRow.Length)
            for ($x = 0; $x -lt $width; $x++) {
                if ($Bitmap.GetPixel($x, $y).A -eq 0) {
                    $byteIndex = [int][Math]::Floor($x / 8)
                    $bitIndex = 7 - ($x % 8)
                    $maskRow[$byteIndex] = [byte]($maskRow[$byteIndex] -bor (1 -shl $bitIndex))
                }
            }

            $binaryWriter.Write($maskRow)
        }

        $binaryWriter.Flush()
        return $memory.ToArray()
    }
    finally {
        $binaryWriter.Dispose()
        $memory.Dispose()
    }
}

try {
    $iconEntries = New-Object System.Collections.Generic.List[object]
    foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
        $bitmap = New-IconBitmap -Source $image -Size $size
        try {
            $bytes = if ($size -eq 256) {
                Convert-BitmapToPngBytes -Bitmap $bitmap
            }
            else {
                Convert-BitmapToIconDibBytes -Bitmap $bitmap
            }

            $iconEntries.Add([pscustomobject]@{
                Size = $size
                Bytes = $bytes
                Offset = 0
            }) | Out-Null
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $offset = 6 + ($iconEntries.Count * 16)
    foreach ($entry in $iconEntries) {
        $entry.Offset = $offset
        $offset += $entry.Bytes.Length
    }

    $stream = [System.IO.File]::Open($IcoPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$iconEntries.Count)

    foreach ($entry in $iconEntries) {
        $directorySize = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$directorySize)
        $writer.Write([byte]$directorySize)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$entry.Bytes.Length)
        $writer.Write([UInt32]$entry.Offset)
    }

    foreach ($entry in $iconEntries) {
        $entryBytes = [byte[]]$entry.Bytes
        $writer.Write($entryBytes, 0, $entryBytes.Length)
    }
}
finally {
    if ($writer -ne $null) { $writer.Dispose() }
    if ($stream -ne $null) { $stream.Dispose() }
    if ($image -ne $null) { $image.Dispose() }
}
