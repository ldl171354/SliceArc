# Smoke test for the colour themes: each palette must load and render.
#
# A palette with a broken key or a bad XAML reference throws XamlParseException
# while the window is being built, so "the window appeared and the log has no
# theme error" is a real check, not a formality.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\theme-smoke-test.ps1
#
param(
    [string]$App = 'D:\SliceArc\artifacts\bin\SliceArc.App.exe',
    [string]$Root = 'D:\SliceArc\artifacts\theme-test'
)

$ErrorActionPreference = 'Stop'
$fail = 0
$themes = @('Light', 'Bandizip', 'Dark', 'Soft')
$appDir = Split-Path -Parent $App
$settings = Join-Path $appDir 'SliceArc.settings.json'
$log = Join-Path $appDir 'SliceArc.log'

function Check([string]$label, [bool]$ok, [string]$detail = '') {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if (-not $ok) { $script:fail++ }
    Write-Output ("  [{0}] {1}{2}" -f $tag, $label, $(if ($detail) { "  -> $detail" } else { '' }))
}

if (-not (Test-Path $App)) { throw "app not found: $App" }
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Path $Root | Out-Null

# A tiny archive so the window has something to list.
Set-Content -LiteralPath (Join-Path $Root 'a.txt') -Value 'theme smoke' -NoNewline
$archive = Join-Path $Root 't.7z'
$p = Start-Process -FilePath $App -ArgumentList @('--compress', ('"' + $archive + '"'), '--add', ('"' + (Join-Path $Root 'a.txt') + '"'), '--format', '7z', '--quiet') -Wait -PassThru
Check 'seed archive created' ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $archive)) "exit=$($p.ExitCode)"

foreach ($theme in $themes) {
    Write-Output ''
    Write-Output "=== theme: $theme ==="

    @{
        LastDirectory        = $Root
        DeleteSpentVolumes   = $false
        KeepLastVolumes      = 1
        Overwrite            = $true
        WindowWidth          = 960
        WindowHeight         = 600
        Theme                = $theme
        ShowCompletionDialog = $false
        PlaySoundOnComplete  = $false
        WelcomeShown         = $true
    } | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding UTF8

    if (Test-Path $log) { Remove-Item -LiteralPath $log -Force }

    $gui = Start-Process -FilePath $App -ArgumentList ('"' + $archive + '"') -PassThru
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $gui.Refresh()
        if ($gui.HasExited) { break }
        if ($gui.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $gui.MainWindowHandle; break }
    }

    Check "$theme 窗口成功创建" ($hwnd -ne [IntPtr]::Zero -and -not $gui.HasExited) "hwnd=$hwnd"

    Start-Sleep -Milliseconds 600
    $logText = if (Test-Path $log) { (Get-Content $log) -join "`n" } else { '' }
    Check "$theme 没有主题/解析错误" ($logText -notmatch '主题|XamlParseException|加载失败') `
        $(if ($logText -match 'ERROR') { ($logText -split "`n" | Select-String 'ERROR' | Select-Object -First 1) } else { 'clean' })

    $gui.Refresh()
    if (-not $gui.HasExited) { $gui.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 600 }
    $gui.Refresh()
    if (-not $gui.HasExited) { Stop-Process -Id $gui.Id -Force }
}

Write-Output ''
if ($fail -eq 0) { Write-Output '===== THEME SMOKE: all passed =====' } else { Write-Output "===== THEME SMOKE: $fail failed =====" }
exit $fail
