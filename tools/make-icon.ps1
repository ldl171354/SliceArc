# Regenerates the SliceArc app icon from the 3D artwork in tools\icon-source.png.
#
# The artwork is a rendered stack of archive blocks with a lightning bolt,
# already cut out of its background and centre-framed in a square (that cut-out
# is done once, offline - see the note in the file header below). This script
# only puts it on the brand tile and writes the size ladder, so re-running it
# never touches the artwork's pixels beyond scaling.
#
# The tile stays light (near-white with a cool blue tint) rather than brand
# blue: the artwork itself is blue, so a blue tile would swallow its silhouette.
# A hairline border keeps the tile defined when it sits on a white page.
#
# Outputs:
#   src\SliceArc.App\Assets\SliceArc.ico   - multi-size ICO (PNG-compressed entries)
#   src\SliceArc.ShellExt\Assets\Logo.png  - 256x256 master for the sparse package
#   docs\appicon.png                       - 256x256 for the landing page
#   docs\favicon.png                       - 64x64 favicon
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $PSScriptRoot "icon-source.png"
if (-not (Test-Path $sourcePath)) { throw "artwork not found: $sourcePath" }

# ---- brand tile palette -----------------------------------------------------
$TileTop    = [System.Drawing.Color]::FromArgb(255, 251, 253, 255)
$TileBottom = [System.Drawing.Color]::FromArgb(255, 228, 237, 251)
$TileEdge   = [System.Drawing.Color]::FromArgb(40, 20, 46, 166)
$Sheen      = [System.Drawing.Color]::FromArgb(150, 255, 255, 255)
$SheenOff   = [System.Drawing.Color]::FromArgb(0, 255, 255, 255)

# How much of the tile the artwork square covers. The artwork carries a small
# transparent margin of its own, so these land the visible subject at ~86% (big
# sizes, where the tile border and sheen need room) and ~95% (16-32 px, where
# every pixel of subject counts and the tile detail is dropped anyway).
$FillNormal = 0.96
$FillSmall  = 1.04

function New-RoundRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-DiagonalBrush([float]$x, [float]$y, [float]$w, [float]$h, $c1, $c2) {
    $rect = [System.Drawing.RectangleF]::new($x, $y, $w, $h)
    return [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect, $c1, $c2, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
}

function New-VerticalBrush([float]$x, [float]$y, [float]$w, [float]$h, $c1, $c2) {
    $rect = [System.Drawing.RectangleF]::new($x, $y, $w, $h)
    return [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect, $c1, $c2, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
}

# Draws one complete icon at exactly $Size, straight from the 512 px artwork, so
# no size in the ladder is produced by resampling another icon.
function Draw-Icon([int]$Size, $Artwork) {
    $detailed = $Size -gt 32
    $bmp = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # geometry is expressed against the 256 px original, then scaled
    $k = $Size / 256.0
    $tx = 12 * $k; $ty = 12 * $k; $tw = 232 * $k; $tr = 52 * $k

    # ---- tile ----
    $tilePath = New-RoundRectPath $tx $ty $tw $tw $tr
    $tileBrush = New-DiagonalBrush $tx $ty $tw $tw $TileTop $TileBottom
    $g.FillPath($tileBrush, $tilePath)
    $tileBrush.Dispose()

    if ($detailed) {
        # Top-left sheen: turns the flat fill into a lit surface.
        $g.SetClip($tilePath)
        $sheenBrush = New-VerticalBrush $tx $ty $tw (150 * $k) $Sheen $SheenOff
        $g.FillRectangle($sheenBrush, [System.Drawing.RectangleF]::new($tx, $ty, $tw, 150 * $k))
        $sheenBrush.Dispose()
        $g.ResetClip()
    }

    # ---- artwork ----
    $fill = if ($detailed) { $FillNormal } else { $FillSmall }
    $aw = $tw * $fill
    $ax = $tx + ($tw - $aw) / 2
    $ay = $ty + ($tw - $aw) / 2 + (2 * $k)
    $g.DrawImage($Artwork, [System.Drawing.RectangleF]::new($ax, $ay, $aw, $aw))

    if ($detailed) {
        # Hairline border: keeps the tile readable when it sits on white.
        $pen = [System.Drawing.Pen]::new($TileEdge, [float]([Math]::Max(1, 1.4 * $k)))
        $g.DrawPath($pen, $tilePath)
        $pen.Dispose()
    }

    $tilePath.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

# ---------------------------------------------------------------------------

Write-Host "Loading artwork $sourcePath" -ForegroundColor Cyan
$artwork = [System.Drawing.Image]::FromFile($sourcePath)

# 256 px master: sparse-package logo + landing-page icon
$master = Draw-Icon 256 $artwork
$logoPath = Join-Path $root "src\SliceArc.ShellExt\Assets\Logo.png"
$master.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Logo.png    -> $logoPath (256 px)" -ForegroundColor Green

$docsDir = Join-Path $root "docs"
$appiconPath = Join-Path $docsDir "appicon.png"
$master.Save($appiconPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "appicon.png -> $appiconPath (256 px)" -ForegroundColor Green

$favicon = Draw-Icon 64 $artwork
$faviconPath = Join-Path $docsDir "favicon.png"
$favicon.Save($faviconPath, [System.Drawing.Imaging.ImageFormat]::Png)
$favicon.Dispose()
Write-Host "favicon.png -> $faviconPath (64 px)" -ForegroundColor Green

# Multi-size ICO (PNG-compressed entries; Windows Vista+). Each size is drawn
# from the artwork directly rather than downscaled from the 256 px master.
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    if ($s -eq 256) { $b = $master }
    else { $b = Draw-Icon $s $artwork }
    $pngs += , (Get-PngBytes $b)
    $b.Dispose()
}
$artwork.Dispose()

$icoPath = Join-Path $root "src\SliceArc.App\Assets\SliceArc.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = [System.IO.BinaryWriter]::new($fs)

# ICONDIR
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
    $bw.Write([byte]$dim)          # width  (0 = 256)
    $bw.Write([byte]$dim)          # height
    $bw.Write([byte]0)             # palette colors
    $bw.Write([byte]0)             # reserved
    $bw.Write([uint16]1)           # color planes
    $bw.Write([uint16]32)          # bits per pixel
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()

Write-Host "SliceArc.ico -> $icoPath ($($sizes -join ', ') px)" -ForegroundColor Green
Write-Host "Done. Rebuild the app to embed the new icon." -ForegroundColor Green
