# RAR 分卷回收端到端测试：独立卷布局下「解压后删除已用完的分卷」必须
#   (1) 绝不中途删卷（中途删会让解码器读到不存在的卷 → 7z 报"数据损坏/不完整"）
#   (2) 整包成功后一次清干净，只留保险丝卷
#   (3) 产物逐字节正确
#
# 只依赖 WinRAR 的命令行造包；没有 Rar.exe 就跳过（RAR 无法用 7z.exe 创建）。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\rar-sweep-e2e-test.ps1
#
param(
    [string]$App = 'D:\SliceArc\artifacts\bin\SliceArc.App.exe',
    [string]$Root = 'D:\SliceArc\artifacts\rar-sweep-e2e',
    [string]$RarExe = 'C:\Program Files\WinRAR\Rar.exe'
)

$ErrorActionPreference = 'Stop'
$fail = 0

function Check([string]$label, [bool]$ok, [string]$detail = '') {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if (-not $ok) { $script:fail++ }
    Write-Output ("  [{0}] {1}{2}" -f $tag, $label, $(if ($detail) { "  -> $detail" } else { '' }))
}

function Invoke-App([string[]]$Arguments) {
    $p = Start-Process -FilePath $App -ArgumentList $Arguments -Wait -PassThru
    return $p.ExitCode
}

if (-not (Test-Path $App)) { throw "app not found: $App" }
if (-not (Test-Path $RarExe)) {
    Write-Output "跳过：没有 WinRAR 命令行（$RarExe），造不出 RAR 分卷"
    exit 0
}

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$data = Join-Path $Root 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null

# 半可压缩的大文件（每个都跨多卷）+ 小文件，尽量贴近真实资源包
$rnd = New-Object System.Random 20261013
for ($i = 1; $i -le 4; $i++) {
    $buf = New-Object byte[] (8MB)
    $rnd.NextBytes($buf)
    for ($j = 0; $j -lt $buf.Length; $j += 4) { $buf[$j] = 0 }
    [IO.File]::WriteAllBytes((Join-Path $data ("big{0:d2}.bin" -f $i)), $buf)
}
for ($i = 1; $i -le 6; $i++) {
    Set-Content -LiteralPath (Join-Path $data ("small{0:d2}.txt" -f $i)) -Value ('slicearc ' * 400) -NoNewline
}

# 造包：自解压 + 2MB 一卷 + 压缩
Push-Location $data
& $RarExe a -m1 -v2m -sfx (Join-Path $Root 'Build.part01.exe') * 2>&1 | Out-Null
Pop-Location

$volumes = @(Get-ChildItem $Root -Filter 'Build.*' | Sort-Object Name)
Check '造出多卷自解压 RAR' ($volumes.Count -gt 3) "$($volumes.Count) 卷，首卷 $($volumes[0].Name)"
if ($volumes.Count -le 3) { exit 1 }

# 记录原始哈希后清掉源文件（模拟"只有压缩包"）
$expected = @{}
Get-ChildItem $data | ForEach-Object { $expected[$_.Name] = (Get-FileHash $_.FullName).Hash }
Get-ChildItem $data | Remove-Item -Force -Recurse

$log = Join-Path (Split-Path -Parent $App) 'SliceArc.log'
if (Test-Path $log) { Remove-Item $log -Force }

# =====================================================================
Write-Output ''
Write-Output '=== 1. 解压 + 边解边删（右键同一套参数）==='
$code = Invoke-App @('--extract', ('"' + $volumes[0].FullName + '"'), '--here', '--delete-spent', '--quiet')

$logText = if (Test-Path $log) { (Get-Content $log) -join "`n" } else { '' }
Check '退出码为 0' ($code -eq 0) "exit=$code"
Check '日志里没有"损坏 / 不完整 / 中断"字样' `
    (-not ($logText -match '损坏|不完整|中断|失败')) `
    $(if ($logText -match '原因：(.+)') { $Matches[1].Trim() } else { '(无错误)' })

# =====================================================================
Write-Output ''
Write-Output '=== 2. 产物逐字节一致 ==='
$bad = 0
foreach ($name in $expected.Keys) {
    $disk = Join-Path $Root $name
    if (-not (Test-Path -LiteralPath $disk)) { $bad++; continue }
    if ((Get-FileHash -LiteralPath $disk).Hash -ne $expected[$name]) { $bad++ }
}
Check "全部 $($expected.Count) 个文件都在且哈希一致" ($bad -eq 0) $(if ($bad) { "$bad 个不符/缺失" } else { 'OK' })

# =====================================================================
Write-Output ''
Write-Output '=== 3. 分卷回收：只剩保险丝（最后 1 卷）==='
$left = @(Get-ChildItem $Root -Filter 'Build.*' -EA SilentlyContinue | Sort-Object Name)
Check '回收了 N-1 卷' ($left.Count -eq 1) "剩 $($left.Count) / $($volumes.Count) 卷：$(($left.Name) -join ', ')"
Check '留下的是最后一卷（保险丝）' `
    ($left.Count -eq 1 -and $left[0].Name -eq $volumes[-1].Name) `
    $(if ($left.Count -eq 1) { $left[0].Name } else { '-' })

Write-Output ''
if ($fail -eq 0) { Write-Output '===== RAR SWEEP E2E: all passed =====' } else { Write-Output "===== RAR SWEEP E2E: $fail failed =====" }
Write-Output "work dir: $Root"
exit $fail
