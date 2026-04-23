# Rebuilds bimy-logo.ico from bimy-logo.png. Run this whenever the source PNG
# changes so the installer EXE and Add/Remove Programs entry pick up the new
# logo on the next installer rebuild.
#
# Produces a multi-frame Vista-style ICO (PNG-compressed entries at 16, 32, 48,
# 256). Windows Vista+ and modern Explorer pick the frame that best matches the
# current DPI scaling, so the icon stays crisp on HiDPI displays.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File Resources\generate-ico.ps1
#
# The source PNG may be any aspect ratio; non-square logos are centered on a
# transparent square canvas so Explorer doesn't stretch them.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src       = Join-Path $scriptDir 'bimy-logo.png'
$dst       = Join-Path $scriptDir 'bimy-logo.ico'
$sizes     = @(16, 32, 48, 256)

if (-not (Test-Path $src)) {
    throw "Source PNG not found at $src. Drop a bimy-logo.png in Resources/ before running this script."
}

$source = [System.Drawing.Image]::FromFile($src)
try {
    # For each target size, render the logo centered on a transparent square
    # canvas and capture the PNG bytes in-memory.
    $frames = @{}
    foreach ($size in $sizes) {
        $canvas = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)

            # Fit the logo into the square while preserving aspect ratio.
            $scale = [Math]::Min($size / $source.Width, $size / $source.Height)
            $w = [int]([Math]::Round($source.Width  * $scale))
            $h = [int]([Math]::Round($source.Height * $scale))
            $x = [int](($size - $w) / 2)
            $y = [int](($size - $h) / 2)
            $g.DrawImage($source, $x, $y, $w, $h)
        } finally {
            $g.Dispose()
        }

        $ms = New-Object System.IO.MemoryStream
        $canvas.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames[$size] = $ms.ToArray()
        $canvas.Dispose()
    }
} finally {
    $source.Dispose()
}

# Write the .ico file. Format spec:
#   - 6-byte ICONDIR header
#   - N × 16-byte ICONDIRENTRY records
#   - N × raw PNG blobs (for Vista-style PNG-compressed icons)
# Width/Height bytes use 0 to mean 256, so `($size -band 0xFF)` is correct for
# every size in { 16, 32, 48, 256 }.
$fs = [System.IO.File]::Create($dst)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0)             # reserved
    $bw.Write([uint16]1)             # 1 = icon, 2 = cursor
    $bw.Write([uint16]$sizes.Count)  # frame count

    $dataOffset = 6 + ($sizes.Count * 16)
    foreach ($size in $sizes) {
        $bytes = $frames[$size]
        $bw.Write([byte]($size -band 0xFF))   # width  (0 = 256)
        $bw.Write([byte]($size -band 0xFF))   # height (0 = 256)
        $bw.Write([byte]0)                    # color-palette count (0 for true colour)
        $bw.Write([byte]0)                    # reserved, must be 0
        $bw.Write([uint16]1)                  # color planes
        $bw.Write([uint16]32)                 # bits per pixel
        $bw.Write([uint32]$bytes.Length)      # image byte size
        $bw.Write([uint32]$dataOffset)        # offset from start of file
        $dataOffset += $bytes.Length
    }

    foreach ($size in $sizes) {
        $bw.Write($frames[$size])
    }
} finally {
    $bw.Dispose()
    $fs.Dispose()
}

Write-Host "Wrote $dst ($([Math]::Round((Get-Item $dst).Length / 1024, 1)) KB, frames: $($sizes -join ', '))"
