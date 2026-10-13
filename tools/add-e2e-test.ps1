# End-to-end test for "add files to an existing archive" (--add-to).
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as GBK).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\add-e2e-test.ps1
#
param(
    [string]$App = 'D:\SliceArc\artifacts\bin\SliceArc.App.exe',
    [string]$Root = 'D:\SliceArc\artifacts\add-test'
)

$ErrorActionPreference = 'Stop'
$fail = 0
$log = Join-Path (Split-Path -Parent $App) 'SliceArc.log'

function Check([string]$label, [bool]$ok, [string]$detail = '') {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if (-not $ok) { $script:fail++ }
    Write-Output ("  [{0}] {1}{2}" -f $tag, $label, $(if ($detail) { "  -> $detail" } else { '' }))
}

# A WinExe must be started with Start-Process -Wait; '&' returns immediately.
function Invoke-App([string[]]$Arguments) {
    $p = Start-Process -FilePath $App -ArgumentList $Arguments -Wait -PassThru
    return $p.ExitCode
}

function Log-Tail([int]$n = 6) {
    if (Test-Path $log) { return (Get-Content $log -Tail $n) -join ' | ' }
    return '(no log)'
}

function Hash-Of([string]$path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }

# Hash every file under a directory, keyed by relative path (forward slashes).
function Tree-Hash([string]$dir) {
    $map = @{}
    if (-not (Test-Path $dir)) { return $map }
    Get-ChildItem -LiteralPath $dir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($dir.Length).TrimStart('\') -replace '\\', '/'
        $map[$rel] = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    }
    return $map
}

function Compare-Tree([string]$expectedDir, [string]$actualDir, [string]$label) {
    $exp = Tree-Hash $expectedDir
    $act = Tree-Hash $actualDir
    $missing = @($exp.Keys | Where-Object { -not $act.ContainsKey($_) })
    $extra = @($act.Keys | Where-Object { -not $exp.ContainsKey($_) })
    $diff = @($exp.Keys | Where-Object { $act.ContainsKey($_) -and $act[$_] -ne $exp[$_] })
    Check "$label 内容逐字节一致" ($missing.Count -eq 0 -and $extra.Count -eq 0 -and $diff.Count -eq 0) `
        ("exp={0} act={1} missing=[{2}] extra=[{3}] diff=[{4}]" -f $exp.Count, $act.Count, `
         ($missing -join ','), ($extra -join ','), ($diff -join ','))
}

if (-not (Test-Path $App)) { throw "app not found: $App" }
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Path $Root | Out-Null

# =====================================================================
Write-Output '=== 0. test data ==='
$src1 = Join-Path $Root 'src1'
$src2 = Join-Path $Root 'src2'
New-Item -ItemType Directory -Path (Join-Path $src1 'sub') -Force | Out-Null
New-Item -ItemType Directory -Path $src2 -Force | Out-Null

Set-Content -LiteralPath (Join-Path $src1 'alpha.txt') -Value ('alpha-' + ('A' * 500)) -NoNewline
$rnd = New-Object System.Random(20261013)
$beta = New-Object byte[] (256 * 1024); $rnd.NextBytes($beta)
[IO.File]::WriteAllBytes((Join-Path $src1 'sub\beta.bin'), $beta)
Set-Content -LiteralPath (Join-Path $src2 'gamma.txt') -Value ('gamma-v1-' + ('G' * 300)) -NoNewline
$delta = New-Object byte[] (128 * 1024); $rnd.NextBytes($delta)
[IO.File]::WriteAllBytes((Join-Path $src2 'delta.bin'), $delta)
Check 'test tree created' ((Get-ChildItem $src1 -Recurse -File).Count -eq 2 -and (Get-ChildItem $src2 -File).Count -eq 2)

# =====================================================================
Write-Output ''
Write-Output '=== 1. create a.7z from src1, then --add-to src2 ==='
$a7z = Join-Path $Root 'a.7z'
$code = Invoke-App @('--compress', ('"' + $a7z + '"'), '--add', ('"' + $src1 + '"'), '--format', '7z', '--quiet')
Check '7z compress exit 0' ($code -eq 0) "exit=$code :: $(Log-Tail 3)"
Check 'a.7z created' (Test-Path -LiteralPath $a7z)

$code = Invoke-App @('--add-to', ('"' + $a7z + '"'), '--add', ('"' + $src2 + '"'), '--quiet')
Check '7z add-to exit 0' ($code -eq 0) "exit=$code :: $(Log-Tail 3)"

$outA = Join-Path $Root 'outA'
$code = Invoke-App @('--extract', ('"' + $a7z + '"'), '--out', ('"' + $outA + '"'), '--quiet')
Check 'extract exit 0' ($code -eq 0) "exit=$code"

# src1 was added as a folder -> entries live under "src1\"; same for src2.
Compare-Tree $src1 (Join-Path $outA 'src1') '1.1 src1 (kept branch)'
Compare-Tree $src2 (Join-Path $outA 'src2') '1.2 src2 (added branch)'

# =====================================================================
Write-Output ''
Write-Output '=== 2. same-name replace (7-Zip a semantics) ==='
$r7z = Join-Path $Root 'r.7z'
$payload = Join-Path $Root 'payload.txt'
Set-Content -LiteralPath $payload -Value ('v1-' + ('P' * 200)) -NoNewline
$code = Invoke-App @('--compress', ('"' + $r7z + '"'), '--add', ('"' + $payload + '"'), '--format', '7z', '--quiet')
Check 'compress single file exit 0' ($code -eq 0) "exit=$code"

Set-Content -LiteralPath $payload -Value ('v2-' + ('Q' * 900)) -NoNewline
$code = Invoke-App @('--add-to', ('"' + $r7z + '"'), '--add', ('"' + $payload + '"'), '--quiet')
Check 'replace add-to exit 0' ($code -eq 0) "exit=$code :: $(Log-Tail 3)"

$outR = Join-Path $Root 'outR'
$code = Invoke-App @('--extract', ('"' + $r7z + '"'), '--out', ('"' + $outR + '"'), '--quiet')
Check 'extract after replace exit 0' ($code -eq 0)

$got = Join-Path $outR 'payload.txt'
Check 'entry count did not grow (still 1 file)' ((Get-ChildItem $outR -Recurse -File).Count -eq 1) `
    ("$((Get-ChildItem $outR -Recurse -File).Count) files")
Check 'replaced content is the new one' ((Test-Path $got) -and (Hash-Of $got) -eq (Hash-Of $payload)) `
    $(if (Test-Path $got) { (Get-Content $got -Raw).Substring(0, 4) } else { '(missing)' })

# =====================================================================
Write-Output ''
Write-Output '=== 3. zip round trip ==='
$z1 = Join-Path $Root 'z1'
New-Item -ItemType Directory -Path $z1 -Force | Out-Null
Set-Content -LiteralPath (Join-Path $z1 'one.txt') -Value ('one-' + ('1' * 100)) -NoNewline
$z2 = Join-Path $Root 'z2'
New-Item -ItemType Directory -Path $z2 -Force | Out-Null
Set-Content -LiteralPath (Join-Path $z2 'two.txt') -Value ('two-' + ('2' * 100)) -NoNewline

$zip = Join-Path $Root 'z.zip'
$code = Invoke-App @('--compress', ('"' + $zip + '"'), '--add', ('"' + $z1 + '"'), '--format', 'zip', '--quiet')
Check 'zip compress exit 0' ($code -eq 0) "exit=$code"
$code = Invoke-App @('--add-to', ('"' + $zip + '"'), '--add', ('"' + $z2 + '"'), '--quiet')
Check 'zip add-to exit 0' ($code -eq 0) "exit=$code :: $(Log-Tail 3)"

$outZ = Join-Path $Root 'outZ'
$code = Invoke-App @('--extract', ('"' + $zip + '"'), '--out', ('"' + $outZ + '"'), '--quiet')
Check 'zip extract exit 0' ($code -eq 0)
Compare-Tree $z1 (Join-Path $outZ 'z1') '3.1 zip kept branch'
Compare-Tree $z2 (Join-Path $outZ 'z2') '3.2 zip added branch'

# =====================================================================
Write-Output ''
Write-Output '=== 4. tar round trip ==='
$t1 = Join-Path $Root 't1'
New-Item -ItemType Directory -Path $t1 -Force | Out-Null
Set-Content -LiteralPath (Join-Path $t1 't.txt') -Value ('tar-' + ('T' * 80)) -NoNewline
$t2 = Join-Path $Root 't2'
New-Item -ItemType Directory -Path $t2 -Force | Out-Null
Set-Content -LiteralPath (Join-Path $t2 'u.txt') -Value ('tar2-' + ('U' * 80)) -NoNewline

$tar = Join-Path $Root 't.tar'
$code = Invoke-App @('--compress', ('"' + $tar + '"'), '--add', ('"' + $t1 + '"'), '--format', 'tar', '--quiet')
Check 'tar compress exit 0' ($code -eq 0) "exit=$code"
$code = Invoke-App @('--add-to', ('"' + $tar + '"'), '--add', ('"' + $t2 + '"'), '--quiet')
Check 'tar add-to exit 0' ($code -eq 0) "exit=$code :: $(Log-Tail 3)"

$outT = Join-Path $Root 'outT'
$code = Invoke-App @('--extract', ('"' + $tar + '"'), '--out', ('"' + $outT + '"'), '--quiet')
Check 'tar extract exit 0' ($code -eq 0)
Compare-Tree $t1 (Join-Path $outT 't1') '4.1 tar kept branch'
Compare-Tree $t2 (Join-Path $outT 't2') '4.2 tar added branch'

# =====================================================================
Write-Output ''
Write-Output '=== 5. unsupported formats must not touch the original ==='
$unsupported = @('x.rar', 'x.gz', 'x.bz2', 'x.xz', 'x.zst', 'x.lz4', 'x.br', 'x.lz', 'x.tar.gz')
foreach ($name in $unsupported) {
    $path = Join-Path $Root $name
    Set-Content -LiteralPath $path -Value ('not really an archive: ' + $name) -NoNewline
    $beforeHash = Hash-Of $path
    $beforeTime = (Get-Item -LiteralPath $path).LastWriteTimeUtc

    $newFile = Join-Path $Root ('payload-for-' + ($name -replace '[.]', '_') + '.txt')
    Set-Content -LiteralPath $newFile -Value 'payload' -NoNewline

    $code = Invoke-App @('--add-to', ('"' + $path + '"'), '--add', ('"' + $newFile + '"'), '--quiet')
    $afterHash = Hash-Of $path
    $afterTime = (Get-Item -LiteralPath $path).LastWriteTimeUtc
    $stray = @(Get-ChildItem $Root -Filter '*.tmp-*' -Recurse -EA SilentlyContinue)

    Check "$name 被拒绝（非 0 退出）" ($code -ne 0) "exit=$code"
    Check "$name 原文件未被改动" ($afterHash -eq $beforeHash -and $afterTime -eq $beforeTime)
    Check "$name 没有留下临时文件" ($stray.Count -eq 0) ($stray.Name -join ',')
}

# =====================================================================
Write-Output ''
Write-Output '=== 6. split volume must be refused ==='
$splitDir = Join-Path $Root 'split'
New-Item -ItemType Directory -Path $splitDir -Force | Out-Null
# Incompressible data on purpose: a run of identical bytes compresses below one
# volume, the archive never splits, and this test would silently prove nothing.
$big = New-Object byte[] (300 * 1024)
(New-Object System.Random(4242)).NextBytes($big)
[IO.File]::WriteAllBytes((Join-Path $splitDir 'big.bin'), $big)
$sArc = Join-Path $splitDir 's.7z'
$code = Invoke-App @('--compress', ('"' + $sArc + '"'), '--add', ('"' + (Join-Path $splitDir 'big.bin') + '"'), '--format', '7z', '--split-volume', '102400', '--quiet')
Check 'split compress exit 0' ($code -eq 0) "exit=$code :: $(Log-Tail 3)"

$volumes = @(Get-ChildItem $splitDir -Filter 's.7z.*' | Sort-Object Name)
Check 'split produced volumes' ($volumes.Count -gt 1) ("$($volumes.Count) parts")

if ($volumes.Count -gt 1) {
    $before = @{}
    foreach ($v in $volumes) { $before[$v.Name] = (Hash-Of $v.FullName) }
    $extra = Join-Path $Root 'extra.txt'
    Set-Content -LiteralPath $extra -Value 'extra' -NoNewline

    $code = Invoke-App @('--add-to', ('"' + $volumes[0].FullName + '"'), '--add', ('"' + $extra + '"'), '--quiet')
    Check '分卷 add-to 被拒绝' ($code -ne 0) "exit=$code"
    $same = $true
    foreach ($v in $volumes) { if ((Hash-Of $v.FullName) -ne $before[$v.Name]) { $same = $false } }
    Check '分卷文件全部未被改动' $same
}

# =====================================================================
Write-Output ''
if ($fail -eq 0) { Write-Output '===== ADD E2E: all passed =====' } else { Write-Output "===== ADD E2E: $fail failed =====" }
Write-Output "work dir: $Root"
exit $fail
