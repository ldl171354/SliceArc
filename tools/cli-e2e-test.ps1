# End-to-end test of the exact command line the Explorer right-click verbs run.
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as GBK).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\cli-e2e-test.ps1
#
param(
    [string]$App = 'D:\SliceArc\dist\SliceArc.App.exe',
    [string]$Root = 'D:\SliceArc\artifacts\cli-test'
)

$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$label, [bool]$ok, [string]$detail = '') {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if (-not $ok) { $script:fail++ }
    Write-Output ("  [{0}] {1}{2}" -f $tag, $label, $(if ($detail) { "  -> $detail" } else { '' }))
}

# PowerShell does NOT wait for a WinExe launched with '&', so every invocation
# goes through Start-Process -Wait. Otherwise the assertions race the extraction.
function Invoke-App([string[]]$Arguments) {
    $p = Start-Process -FilePath $App -ArgumentList $Arguments -Wait -PassThru
    return $p.ExitCode
}

if (-not (Test-Path $App)) { throw "app not found: $App" }
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Path $Root | Out-Null

# ---- build a 5 MiB incompressible payload split into 1 MiB volumes ----
$payload = Join-Path $Root 'payload.bin'
$fs = [IO.File]::Create($payload)
$chunk = New-Object byte[] (1MB)
$rng = New-Object System.Random(20261012)
for ($i = 0; $i -lt 5; $i++) {
    $rng.NextBytes($chunk)
    $fs.Write($chunk, 0, $chunk.Length)
}
$fs.Close()

$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
& $sevenZip a -t7z -mx1 -v1m (Join-Path $Root 'sample.7z') $payload | Out-Null
if ($LASTEXITCODE -ne 0) { throw '7z.exe failed' }
Remove-Item $payload

$volumes = Get-ChildItem $Root -Filter 'sample.7z.*' | Sort-Object Name
Write-Output ("=== 1. volume set: {0} parts ===" -f $volumes.Count)
Check 'more than one volume produced' ($volumes.Count -gt 1) ("$($volumes.Count)")

# ---- run the right-click verb: extract into <name>\ and delete spent volumes ----
Write-Output ''
Write-Output '=== 2. --extract --to-subfolder --delete-spent --quiet ==='
$exit = Invoke-App @('--extract', ('"' + (Join-Path $Root 'sample.7z.001') + '"'), '--to-subfolder', '--delete-spent', '--quiet')
Check 'process exit code 0' ($exit -eq 0) ("exit=$exit")

$outFile = Join-Path $Root 'sample\payload.bin'
Check 'payload landed in sample\payload.bin' (Test-Path -LiteralPath $outFile)

if (Test-Path -LiteralPath $outFile) {
    $size = (Get-Item -LiteralPath $outFile).Length
    Check 'payload size is 5 MiB' ($size -eq 5MB) ("$size bytes")

    # rebuild the same payload deterministically to compare
    $expected = Join-Path $Root 'expected.bin'
    $fs2 = [IO.File]::Create($expected)
    $chunk2 = New-Object byte[] (1MB)
    $rng2 = New-Object System.Random(20261012)
    for ($i = 0; $i -lt 5; $i++) {
        $rng2.NextBytes($chunk2)
        $fs2.Write($chunk2, 0, $chunk2.Length)
    }
    $fs2.Close()

    $hashOut = (Get-FileHash -Algorithm SHA256 -LiteralPath $outFile).Hash
    $hashExp = (Get-FileHash -Algorithm SHA256 -LiteralPath $expected).Hash
    Check 'SHA256 matches the original payload' ($hashOut -eq $hashExp) ($hashOut.Substring(0, 16) + '...')
    Remove-Item -LiteralPath $expected
}

# ---- volume reclamation ----
Write-Output ''
Write-Output '=== 3. volume reclamation ==='
$left = @(Get-ChildItem $Root -Filter 'sample.7z.*' | Sort-Object Name)
Check 'only the fuse volume is left' ($left.Count -eq 1) ("$($left.Count) left: " + ($left.Name -join ', '))
Check 'the surviving part is the last one' ($left.Count -eq 1 -and $left[0].Name -eq $volumes[-1].Name) ($left[0].Name)

# ---- batch extraction of a folder ----
Write-Output ''
Write-Output '=== 4. --extract-all --here ==='
$batch = Join-Path $Root 'batch'
New-Item -ItemType Directory -Path $batch | Out-Null
$payload2 = Join-Path $batch 'inner.bin'
$fs3 = [IO.File]::Create($payload2)
$rng3 = New-Object System.Random(7)
for ($i = 0; $i -lt 2; $i++) {
    $chunk3 = New-Object byte[] (1MB)
    $rng3.NextBytes($chunk3)
    $fs3.Write($chunk3, 0, $chunk3.Length)
}
$fs3.Close()
& $sevenZip a -t7z -mx1 -v512k (Join-Path $batch 'bundle.7z') $payload2 | Out-Null
Remove-Item -LiteralPath $payload2

$exit = Invoke-App @('--extract-all', ('"' + $batch + '"'), '--here', '--quiet')
Check 'batch exit code 0' ($exit -eq 0) ("exit=$exit")
Check 'batch payload extracted' (Test-Path -LiteralPath (Join-Path $batch 'inner.bin'))
Check 'batch payload size is 2 MiB' ((Get-Item -LiteralPath (Join-Path $batch 'inner.bin')).Length -eq 2MB)

# ---- integrity check verb ----
Write-Output ''
Write-Output '=== 5. --test ==='
$exit = Invoke-App @('--extract', ('"' + (Join-Path $batch 'bundle.7z.001') + '"'), '--test', '--quiet')
Check 'test exit code 0' ($exit -eq 0) ("exit=$exit")

Write-Output ''
if ($fail -eq 0) { Write-Output '===== CLI E2E: all passed =====' } else { Write-Output "===== CLI E2E: $fail failed =====" }
Write-Output "work dir: $Root"
exit $fail
