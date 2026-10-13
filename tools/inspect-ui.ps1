# Verifies that the GUI actually renders: launches it, walks the UI Automation
# tree of the main window and prints every control with its name and state.
# This is the headless stand-in for "look at the window".
#
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as GBK).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\inspect-ui.ps1 -Target "D:\path\archive.7z.001"
#
param(
    [string]$App = 'D:\SliceArc\dist\SliceArc.App.exe',
    [string]$Target = '',
    [string]$Out = 'D:\SliceArc\artifacts\ui-tree.txt',
    [int]$WaitSeconds = 7
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$dir = Split-Path -Parent $Out
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$appArgs = @()
if ($Target) { $appArgs += ('"' + $Target + '"') }
$p = Start-Process -FilePath $App -ArgumentList $appArgs -PassThru
Start-Sleep -Seconds $WaitSeconds

$p.Refresh()
if ($p.HasExited) {
    Write-Output "EXITED early, code=$($p.ExitCode)"
    exit 1
}

$hwnd = $p.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Output 'no main window handle'
    Stop-Process -Id $p.Id -Force
    exit 2
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

$lines = New-Object System.Collections.Generic.List[string]
function Walk($element, [int]$depth) {
    if ($null -eq $element -or $depth -gt 12) { return }
    if ($script:lines.Count -gt 400) { return }

    $type = $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    $name = $element.Current.Name
    if ($name.Length -gt 90) { $name = $name.Substring(0, 90) + '...' }
    $state = @()
    if ($element.Current.IsOffscreen) { $state += 'offscreen' }
    if (-not $element.Current.IsEnabled) { $state += 'disabled' }
    if ($element.Current.IsKeyboardFocusable) { $state += 'focusable' }
    $extra = if ($state.Count) { ' [' + ($state -join ',') + ']' } else { '' }

    $script:lines.Add(('{0}{1}: {2}{3}' -f ('  ' * $depth), $type, $name, $extra))

    $child = $walker.GetFirstChild($element)
    while ($null -ne $child) {
        Walk $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

Walk $root 0

$text = ($lines -join [Environment]::NewLine)
Set-Content -LiteralPath $Out -Value $text -Encoding UTF8
Write-Output ("pid={0} title='{1}' controls={2}" -f $p.Id, $p.MainWindowTitle, $lines.Count)
Write-Output $text
Write-Output ("saved {0}" -f $Out)

$p.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Write-Output 'app closed'
