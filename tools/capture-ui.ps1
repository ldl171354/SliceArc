# Launches the GUI, screenshots its window, and leaves the PNG for inspection.
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as GBK).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-ui.ps1 -Target "D:\path\archive.7z.001"
#
param(
    [string]$App = 'D:\SliceArc\dist\SliceArc.App.exe',
    [string]$Target = '',
    [string]$Out = 'D:\SliceArc\artifacts\ui-main.png',
    [int]$WaitSeconds = 6
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct WinRect { public int Left, Top, Right, Bottom; }
public static class WinApi {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out WinRect r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
}
"@

$dir = Split-Path -Parent $Out
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$args = @()
if ($Target) { $args += ('"' + $Target + '"') }
$p = Start-Process -FilePath $App -ArgumentList $args -PassThru
Start-Sleep -Seconds $WaitSeconds

$p.Refresh()
if ($p.HasExited) {
    Write-Output "EXITED early, code=$($p.ExitCode)"
    exit 1
}

$hwnd = $p.MainWindowHandle
Write-Output ("pid={0} hwnd={1} title='{2}'" -f $p.Id, $hwnd, $p.MainWindowTitle)
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Output 'no main window handle'
    Stop-Process -Id $p.Id -Force
    exit 2
}

[WinApi]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 800

$rect = New-Object WinRect
[WinApi]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Output ("window rect {0},{1} {2}x{3}" -f $rect.Left, $rect.Top, $w, $h)

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)

# PrintWindow renders the window even when another window covers it; WPF needs
# the PW_RENDERFULLCONTENT flag (2) to paint into the provided DC.
$hdc = $g.GetHdc()
$ok = [WinApi]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
if (-not $ok) {
    Write-Output 'PrintWindow failed, falling back to screen copy'
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
}

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Output ("saved {0} ({1} bytes)" -f $Out, (Get-Item -LiteralPath $Out).Length)

$p.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Write-Output 'app closed'
