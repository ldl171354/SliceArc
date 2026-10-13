# Regenerates the SliceArc app icon: an archive box with a zipper and a
# lightning bolt, drawn programmatically (no external assets).
#
# Outputs:
#   src\SliceArc.App\Assets\SliceArc.ico   - multi-size ICO (PNG-compressed entries)
#   src\SliceArc.ShellExt\Assets\Logo.png  - 256x256 master for the sparse package
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot

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

function New-LinearBrush([float]$x, [float]$y, [float]$w, [float]$h, $c1, $c2) {
    $rect = [System.Drawing.RectangleF]::new($x, $y, $w, $h)
    return [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect, $c1, $c2, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
}

function Draw-Master {
    $bmp = [System.Drawing.Bitmap]::new(256, 256)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    # ---- soft drop shadow ----
    $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(70, 15, 23, 42))
    $shadowPath = New-RoundRectPath 34 70 188 160 24
    $g.FillPath($shadowBrush, $shadowPath)
    $shadowPath.Dispose(); $shadowBrush.Dispose()

    # ---- box body: blue gradient ----
    $bodyPath = New-RoundRectPath 30 62 188 158 24
    $bodyBrush = New-LinearBrush 30 62 188 158 `
        ([System.Drawing.Color]::FromArgb(96, 165, 250)) `
        ([System.Drawing.Color]::FromArgb(29, 78, 216))
    $g.FillPath($bodyBrush, $bodyPath)
    $bodyBrush.Dispose()

    # ---- lid: lighter top band, clipped to the body ----
    $g.SetClip($bodyPath)
    $lidBrush = New-LinearBrush 30 62 188 46 `
        ([System.Drawing.Color]::FromArgb(147, 197, 253)) `
        ([System.Drawing.Color]::FromArgb(59, 130, 246))
    $g.FillRectangle($lidBrush, [System.Drawing.RectangleF]::new(30, 62, 188, 46))
    $lidBrush.Dispose()
    $g.ResetClip()

    # ---- body outline ----
    $outlinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(30, 58, 138), 3)
    $g.DrawPath($outlinePen, $bodyPath)
    $outlinePen.Dispose()

    # ---- zipper teeth along the lid seam (y ~ 102) ----
    $teethBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(219, 234, 254))
    for ($x = 48; $x -le 200; $x += 16) {
        $tp = New-RoundRectPath $x 97 10 10 3
        $g.FillPath($teethBrush, $tp)
        $tp.Dispose()
    }
    $teethBrush.Dispose()

    # ---- zipper slider in the middle ----
    $sliderPath = New-RoundRectPath 110 91 28 22 6
    $sliderBrush = New-LinearBrush 110 91 28 22 `
        ([System.Drawing.Color]::FromArgb(248, 250, 252)) `
        ([System.Drawing.Color]::FromArgb(148, 163, 184))
    $g.FillPath($sliderBrush, $sliderPath)
    $sliderBrush.Dispose()
    $sliderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(71, 85, 105), 2)
    $g.DrawPath($sliderPen, $sliderPath)
    $sliderPen.Dispose(); $sliderPath.Dispose()

    # ---- lightning bolt ----
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(150, 106),
        [System.Drawing.PointF]::new(100, 178),
        [System.Drawing.PointF]::new(127, 178),
        [System.Drawing.PointF]::new(108, 226),
        [System.Drawing.PointF]::new(174, 146),
        [System.Drawing.PointF]::new(143, 146),
        [System.Drawing.PointF]::new(173, 106)
    )
    $boltBrush = New-LinearBrush 100 106 74 120 `
        ([System.Drawing.Color]::FromArgb(254, 224, 71)) `
        ([System.Drawing.Color]::FromArgb(245, 158, 11))
    $g.FillPolygon($boltBrush, $pts)
    $boltBrush.Dispose()
    $boltPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(146, 64, 14), 4)
    $boltPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPolygon($boltPen, $pts)
    $boltPen.Dispose()

    $bodyPath.Dispose()
    $g.Dispose()
    return $bmp
}

function Resize-Bitmap($src, [int]$size) {
    $dst = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $dst
}

function Get-PngBytes($bmp) {
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

# ---------------------------------------------------------------------------

Write-Host "Drawing master 256x256..." -ForegroundColor Cyan
$master = Draw-Master

# Logo.png for the sparse package
$logoPath = Join-Path $root "src\SliceArc.ShellExt\Assets\Logo.png"
$master.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Logo.png  -> $logoPath" -ForegroundColor Green

# Multi-size ICO (PNG-compressed entries; Windows Vista+)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    if ($s -eq 256) { $b = $master } else { $b = Resize-Bitmap $master $s }
    $pngs += , (Get-PngBytes $b)
    if ($s -ne 256) { $b.Dispose() }
}
$master.Dispose()

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

Write-Host "SliceArc.ico -> $icoPath ($sizes px)" -ForegroundColor Green
Write-Host "Done. Rebuild the app to embed the new icon." -ForegroundColor Green
