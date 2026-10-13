# Compression format matrix test: every format the app offers must really produce
# a valid archive, and the ones 7z.dll cannot write must be refused cleanly.
#
# Verification is done with 7z.exe (and a byte-for-byte round trip), never with
# the code under test.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\compress-formats-test.ps1
#
param(
    [string]$App = 'D:\SliceArc\artifacts\bin\SliceArc.App.exe',
    [string]$Root = 'D:\SliceArc\artifacts\fmt-test'
)

$ErrorActionPreference = 'Stop'
$fail = 0
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
$log = Join-Path (Split-Path -Parent $App) 'SliceArc.log'

function Check([string]$label, [bool]$ok, [string]$detail = '') {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if (-not $ok) { $script:fail++ }
    Write-Output ("  [{0}] {1}{2}" -f $tag, $label, $(if ($detail) { "  -> $detail" } else { '' }))
}

function Invoke-App([string[]]$Arguments) {
    $p = Start-Process -FilePath $App -ArgumentList $Arguments -Wait -PassThru
    return $p.ExitCode
}

function Test-Archive([string]$archive) {
    if (-not (Test-Path -LiteralPath $archive)) { return $false }
    $out = & $sevenZip t -- $archive 2>&1
    return ("$out" -match 'Everything is Ok')
}

if (-not (Test-Path $App)) { throw "app not found: $App" }
if (-not (Test-Path $sevenZip)) { throw "7z.exe not found: $sevenZip" }
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Path $Root | Out-Null

# Incompressible payload: a run of identical bytes would compress to nothing and
# make the size checks meaningless.
$bytes = New-Object byte[] (512 * 1024)
(New-Object System.Random(20261013)).NextBytes($bytes)
[IO.File]::WriteAllBytes((Join-Path $Root 'data.bin'), $bytes)
Set-Content -LiteralPath (Join-Path $Root 'notes.txt') -Value ('slicearc ' * 500) -NoNewline

# =====================================================================
Write-Output '=== 1. container formats (multi-entry) ==='
foreach ($fmt in @('7z', 'zip', 'tar', 'wim')) {
    $archive = Join-Path $Root "multi.$fmt"
    $code = Invoke-App @('--compress', ('"' + $archive + '"'), '--add', ('"' + (Join-Path $Root 'data.bin') + '"'),
        '--add', ('"' + (Join-Path $Root 'notes.txt') + '"'), '--format', $fmt, '--quiet')
    Check "$fmt 创建成功且 7z.exe 校验通过" ($code -eq 0 -and (Test-Archive $archive)) `
        "exit=$code size=$((Get-Item $archive -EA SilentlyContinue).Length)"
}

# =====================================================================
Write-Output ''
Write-Output '=== 2. single-stream formats (one entry) ==='
foreach ($fmt in @('gz', 'bz2', 'xz')) {
    $archive = Join-Path $Root "single.$fmt"
    $code = Invoke-App @('--compress', ('"' + $archive + '"'), '--add', ('"' + (Join-Path $Root 'data.bin') + '"'),
        '--format', $fmt, '--quiet')
    Check "$fmt 创建成功且 7z.exe 校验通过" ($code -eq 0 -and (Test-Archive $archive)) `
        "exit=$code size=$((Get-Item $archive -EA SilentlyContinue).Length)"
}

# =====================================================================
Write-Output ''
Write-Output '=== 3. round trip: extract what we just made and compare bytes ==='
foreach ($fmt in @('7z', 'zip', 'tar', 'wim')) {
    $archive = Join-Path $Root "multi.$fmt"
    if (-not (Test-Path $archive)) { continue }
    $outDir = Join-Path $Root "back-$fmt"
    & $sevenZip x -y -o"$outDir" -- $archive 2>$null | Out-Null
    $same = $true
    foreach ($name in @('data.bin', 'notes.txt')) {
        $a = (Get-FileHash -LiteralPath (Join-Path $Root $name)).Hash
        $b = (Get-FileHash -LiteralPath (Join-Path $outDir $name) -EA SilentlyContinue).Hash
        if ($a -ne $b) { $same = $false }
    }
    Check "$fmt 解压回来逐字节一致" $same
}

# =====================================================================
Write-Output ''
Write-Output '=== 4. zst must be refused (7-Zip 26.04 cannot write zstd) ==='
if (Test-Path $log) { Remove-Item $log -Force }
$zst = Join-Path $Root 'no.zst'
$code = Invoke-App @('--compress', ('"' + $zst + '"'), '--add', ('"' + (Join-Path $Root 'data.bin') + '"'),
    '--format', 'zst', '--quiet')
$logText = if (Test-Path $log) { (Get-Content $log) -join "`n" } else { '' }
Check 'zst 被拒绝（非 0 退出、无产物）' ($code -ne 0 -and -not (Test-Path $zst)) "exit=$code"
Check '拒绝理由里列出了可创建的格式' ($logText -match '可创建的格式：.*7z.*zip') `
    $(if ($logText -match '可创建的格式：(.+)') { $Matches[1].Trim() } else { '(无)' })

# =====================================================================
Write-Output ''
Write-Output '=== 5. no stray temp files left behind ==='
$stray = @(Get-ChildItem $Root -Recurse -Filter '*.tmp-*' -EA SilentlyContinue)
Check '没有临时文件残留' ($stray.Count -eq 0) ($stray.Name -join ',')

Write-Output ''
if ($fail -eq 0) { Write-Output '===== FORMAT MATRIX: all passed =====' } else { Write-Output "===== FORMAT MATRIX: $fail failed =====" }
Write-Output "work dir: $Root"
exit $fail
