# Real drag-and-drop test for "drop files onto the open archive".
#
# The drop is not faked at the WPF level: SliceArc.DropProbe constructs the real
# SliceArc main window in-process and performs DragEnter/DragOver/Drop on that
# window's own OLE drop target with a genuine CF_HDROP data object. Everything a
# mouse drag would trigger runs - WPF drop routing, AllowDrop on the window, the
# Drop handler, and the archive update behind it.
#
# (It must run in-process: the "OleDropTargetInterface" window property is a
# pointer valid only in the owning process. From another process COM cannot be
# handed that pointer at all.)
#
# Verification uses 7z.exe, i.e. not the code under test.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\drag-add-e2e-test.ps1
#
param(
    [string]$Probe = 'D:\SliceArc\tests\SliceArc.DropProbe\bin\Release\net8.0-windows\SliceArc.DropProbe.exe',
    [string]$App = 'D:\SliceArc\artifacts\bin\SliceArc.App.exe',
    [string]$Root = 'D:\SliceArc\artifacts\drag-add-test'
)

$ErrorActionPreference = 'Stop'
$fail = 0
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
$probeDir = Split-Path -Parent $Probe
$log = Join-Path $probeDir 'SliceArc.log'

function Check([string]$label, [bool]$ok, [string]$detail = '') {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if (-not $ok) { $script:fail++ }
    Write-Output ("  [{0}] {1}{2}" -f $tag, $label, $(if ($detail) { "  -> $detail" } else { '' }))
}

function Invoke-App([string[]]$Arguments) {
    $p = Start-Process -FilePath $App -ArgumentList $Arguments -Wait -PassThru
    return $p.ExitCode
}

# Entry paths inside an archive, via 7-Zip itself.
# "7z l -slt" prints the archive's own properties first, then a ---------- line,
# then the entries - the header must not be mistaken for an entry.
function List-Entries([string]$archive) {
    if (-not (Test-Path -LiteralPath $archive)) { return @() }
    $out = & $sevenZip l -slt -- $archive 2>$null
    $started = $false
    $names = @()
    foreach ($line in $out) {
        if (-not $started) {
            if ("$line".Trim() -match '^-{5,}$') { $started = $true }
            continue
        }
        if ("$line" -like 'Path = *') { $names += "$line".Substring(7) }
    }
    return $names
}

function Invoke-Probe([string[]]$Arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & $Probe @Arguments 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    return [pscustomobject]@{ ExitCode = $code; Output = @($out | ForEach-Object { "$_" }) }
}

if (-not (Test-Path $Probe)) { throw "probe not found: $Probe (build tests\SliceArc.DropProbe)" }
if (-not (Test-Path $App)) { throw "app not found: $App" }
if (-not (Test-Path $sevenZip)) { throw "7z.exe not found: $sevenZip" }

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Path $Root | Out-Null

# The probe hosts the app, so AppContext.BaseDirectory is the probe's folder:
# that is where the settings file (and the log) must go.
$json = @{
    LastDirectory        = $Root
    DeleteSpentVolumes   = $false
    KeepLastVolumes      = 1
    Overwrite            = $true
    WindowWidth          = 960
    WindowHeight         = 600
    Theme                = 'Light'
    ShowCompletionDialog = $false
    PlaySoundOnComplete  = $false
    WelcomeShown         = $true
} | ConvertTo-Json
Set-Content -LiteralPath (Join-Path $probeDir 'SliceArc.settings.json') -Value $json -Encoding UTF8

# =====================================================================
Write-Output '=== 0. seed archive ==='
$seedDir = Join-Path $Root 'seed'
New-Item -ItemType Directory -Path $seedDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $seedDir 'seed.txt') -Value ('seed-' + ('S' * 400)) -NoNewline

$archive = Join-Path $Root 'target.7z'
$code = Invoke-App @('--compress', ('"' + $archive + '"'), '--add', ('"' + (Join-Path $seedDir 'seed.txt') + '"'), '--format', '7z', '--quiet')
Check 'seed archive created' ($code -eq 0 -and (Test-Path -LiteralPath $archive)) "exit=$code"

$before = List-Entries $archive
Write-Output ("  before: " + ($before -join ', '))
Check 'seed archive has exactly 1 entry' ($before.Count -eq 1) ("$($before.Count)")

# =====================================================================
Write-Output ''
Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
Write-Output '=== 1. drop a FILE onto the open archive ==='
$dropDir = Join-Path $Root 'drop'
New-Item -ItemType Directory -Path $dropDir -Force | Out-Null
$dropped = Join-Path $dropDir 'dropped.txt'
Set-Content -LiteralPath $dropped -Value ('dropped-' + ('D' * 700)) -NoNewline

$run = Invoke-Probe @($archive, $dropped)
Write-Output ("  probe: " + (($run.Output | Where-Object { "$_" -notmatch '^\[data\]' } | ForEach-Object { "$_".Trim() }) -join ' | '))
Check 'OLE drop accepted (DragEnter/Drop succeeded)' ($run.ExitCode -eq 0) "exit=$($run.ExitCode)"

$after = List-Entries $archive
Write-Output ("  after:  " + ($after -join ', '))
Check 'dropped file is now inside the archive' (@($after | Where-Object { $_ -like '*dropped.txt*' }).Count -ge 1)
Check 'original entry survived the update' (@($after | Where-Object { $_ -like '*seed.txt*' }).Count -ge 1)
Check 'entry count grew by one' ($after.Count -eq $before.Count + 1) ("$($before.Count) -> $($after.Count)")

# Independent content check: extract with 7z.exe and compare bytes.
$outDir = Join-Path $Root 'verify'
& $sevenZip x -y -o"$outDir" -- $archive 2>$null | Out-Null
$droppedName = @($after | Where-Object { $_ -like '*dropped.txt*' })[0]
if ($droppedName) {
    $extracted = Join-Path $outDir ($droppedName -replace '/', '\')
    Check 'dropped content round-trips byte-for-byte' `
        ((Test-Path -LiteralPath $extracted) -and
         ((Get-FileHash -LiteralPath $extracted).Hash -eq (Get-FileHash -LiteralPath $dropped).Hash))
}
else {
    Check 'dropped content round-trips byte-for-byte' $false '(entry not found)'
}

$logText = if (Test-Path $log) { (Get-Content $log) -join "`n" } else { '' }
Check 'app logged the add' ($logText -match '添加到') ''

# =====================================================================
Write-Output ''
Write-Output '=== 2. drop a FOLDER onto the open archive ==='
$folder = Join-Path $Root 'folder'
New-Item -ItemType Directory -Path (Join-Path $folder 'inner') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $folder 'top.txt') -Value 'top' -NoNewline
Set-Content -LiteralPath (Join-Path $folder 'inner\deep.txt') -Value 'deep' -NoNewline

$beforeFolder = List-Entries $archive
$run = Invoke-Probe @($archive, $folder)
Write-Output ("  probe: " + (($run.Output | Where-Object { "$_" -notmatch '^\[data\]' } | ForEach-Object { "$_".Trim() }) -join ' | '))
Check 'folder drop accepted' ($run.ExitCode -eq 0) "exit=$($run.ExitCode)"

$afterFolder = List-Entries $archive
Write-Output ("  entries: " + ($afterFolder -join ', '))
Check 'folder contents were added (incl. the nested file)' `
    (@($afterFolder | Where-Object { $_ -like '*top.txt*' }).Count -ge 1 -and
     @($afterFolder | Where-Object { $_ -like '*deep.txt*' }).Count -ge 1)
Check 'file drop entry still there' (@($afterFolder | Where-Object { $_ -like '*dropped.txt*' }).Count -ge 1)

# =====================================================================
Write-Output ''
Write-Output '=== 3. same-name drop replaces instead of duplicating ==='
Set-Content -LiteralPath $dropped -Value ('dropped-v2-' + ('E' * 900)) -NoNewline
$beforeReplace = List-Entries $archive
$run = Invoke-Probe @($archive, $dropped)
Check 'replace drop accepted' ($run.ExitCode -eq 0) "exit=$($run.ExitCode)"

$afterReplace = List-Entries $archive
Check 'entry count unchanged after same-name drop' ($afterReplace.Count -eq $beforeReplace.Count) `
    ("$($beforeReplace.Count) -> $($afterReplace.Count)")

$outDir2 = Join-Path $Root 'verify2'
& $sevenZip x -y -o"$outDir2" -- $archive 2>$null | Out-Null
$name2 = @($afterReplace | Where-Object { $_ -like '*dropped.txt*' })[0]
if ($name2) {
    $file2 = Join-Path $outDir2 ($name2 -replace '/', '\')
    Check 'replaced content is the new one' `
        ((Test-Path -LiteralPath $file2) -and
         ((Get-FileHash -LiteralPath $file2).Hash -eq (Get-FileHash -LiteralPath $dropped).Hash))
}
else {
    Check 'replaced content is the new one' $false '(entry not found)'
}

Write-Output ''
if ($fail -eq 0) { Write-Output '===== DRAG-ADD E2E: all passed =====' } else { Write-Output "===== DRAG-ADD E2E: $fail failed =====" }
Write-Output "work dir: $Root"
exit $fail
