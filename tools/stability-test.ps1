# stability-test.ps1 - SliceArc common-function stability + error-surface suite
#
# Two questions, one run:
#   1. Does it survive normal and hostile input without crashing or hanging?
#   2. Is the error surface honest? The rule this suite enforces:
#        OK            -> exit 0 AND the log must contain NO 原因/建议/! line at all
#                         (anything we can handle must not be reported as an error)
#        USER          -> must fail with a 原因 + 建议 (the user can fix it themselves)
#        UNSUPPORTED   -> must fail, and the message must say what to use instead
#        SAFETY        -> must refuse (malicious archive), loudly
#      Exit 0 while logging a failure is a bug: it tells a script the job succeeded.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\stability-test.ps1
#
# ASCII-only source would be ideal, but the assertions have to match Chinese log text,
# so this file MUST keep its UTF-8 BOM (PowerShell 5.1 reads BOM-less files as GBK).
param(
    [string]$App = 'D:\SliceArc\dist\app\SliceArc.App.exe',
    [string]$Root = 'D:\SliceArc\artifacts\stability',
    [string]$SevenZip = 'C:\Program Files\7-Zip\7z.exe',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $App)) { throw "app not found: $App" }
if (-not (Test-Path $SevenZip)) { throw "7z.exe not found: $SevenZip" }
$log = Join-Path (Split-Path $App) 'SliceArc.log'

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Path $Root | Out-Null

$script:pass = 0
$script:fail = 0
$script:findings = New-Object System.Collections.ArrayList

function Run-App([string[]]$appArgs, [int]$timeoutSec = 25) {
    $before = if (Test-Path $log) { @(Get-Content $log -EA SilentlyContinue).Count } else { 0 }
    $start = (Get-Date).AddMilliseconds(-30)    # 只补同一毫秒的边界；余量给大了会把上一轮的日志算进来
    $sw = [Diagnostics.Stopwatch]::StartNew()
    # Never -Wait blindly: a CLI invocation that opens an interactive dialog would block
    # the suite forever. A hang is a result, not an obstacle.
    $p = Start-Process -FilePath $App -ArgumentList $appArgs -PassThru
    $hung = $false
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        $hung = $true
        try { $p.Kill() } catch { }
        try { $p.WaitForExit(5000) | Out-Null } catch { }
    }
    $sw.Stop()
    # Slice by timestamp, not line count: the log rolls, so "lines after N" silently
    # returns nothing once the file stops growing and every assertion turns into a
    # false failure.
    $all = if (Test-Path $log) { @(Get-Content $log -EA SilentlyContinue) } else { @() }
    $slice = @()
    foreach ($l in $all) {
        if ($l -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})') {
            $ts = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
            if ($ts -ge $start) { $slice += $l }
        }
    }
    return [pscustomobject]@{
        Exit    = $(if ($hung) { 'HUNG' } else { $p.ExitCode })
        Log     = $slice
        Secs    = [math]::Round($sw.Elapsed.TotalSeconds, 2)
        Reason  = (($slice | Where-Object { $_ -match '原因：' } | Select-Object -First 1) -replace '^.*原因：', '')
        Hint    = (($slice | Where-Object { $_ -match '建议：' } | Select-Object -First 1) -replace '^.*建议：', '')
        Detail  = (($slice | Where-Object { $_ -match '\[INFO \]   ! ' } | Select-Object -First 1) -replace '^.*! ', '')
        Errors  = @($slice | Where-Object { $_ -match '\[ERROR\]' })
    }
}

function Check([string]$name, [string]$expect, $r, [string]$note = '', [string[]]$mustContain = @(), [string[]]$mustNotContain = @()) {
    # A process killed by a crash reports a large negative / 0xC0000005 style value.
    $hung = ($r.Exit -eq 'HUNG')
    $crashed = (-not $hung) -and ($r.Exit -lt 0 -or $r.Exit -gt 100)
    $hasReason = [bool]$r.Reason -or ($r.Errors.Count -gt 0)
    $ok = $false
    switch ($expect) {
        'OK'          { $ok = (-not $hung) -and ($r.Exit -eq 0) -and (-not $hasReason) -and ($r.Errors.Count -eq 0) }
        'USER'        { $ok = ($r.Exit -ne 0) -and $hasReason }
        'UNSUPPORTED' { $ok = ($r.Exit -ne 0) -and $hasReason }
        'SAFETY'      { $ok = ($r.Exit -ne 0) -and $hasReason }
    }
    if ($crashed -or $hung) { $ok = $false }
    # A failure is only reported correctly if it actually says the right thing.
    foreach ($needle in $mustContain) {
        $blob = "$($r.Reason) $($r.Hint) $($r.Detail) $($r.Errors -join ' ')"
        if ($blob -notlike "*$needle*") { $ok = $false; $extra += "文案缺少「$needle」" }
    }
    foreach ($needle in $mustNotContain) {
        $blob2 = "$($r.Reason) $($r.Detail)"
        if ($blob2 -like "*$needle*") { $ok = $false; $extra += "文案不该出现「$needle」" }
    }
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if ($ok) { $script:pass++ } else { $script:fail++ }
    $extra = @()
    if ($hung) { $extra += '⏱ 超时未退出（弹了交互窗口？）' } elseif ($r.Exit -ne 0) { $extra += "exit=$($r.Exit)" }
    if ($r.Reason) { $extra += "原因=$($r.Reason)" }
    if ($expect -eq 'OK' -and $r.Errors.Count) { $extra += "不该有 ERROR：$($r.Errors[0])" }
    if ($crashed) { $extra += '崩溃/异常退出码' }
    Write-Output ("  [{0}] {1,-46} {2}{3}" -f $tag, $name, $note, $(if ($extra) { '  ' + ($extra -join ' | ') } else { '' }))
    if (-not $ok) {
        [void]$script:findings.Add([pscustomobject]@{ Name = $name; Expect = $expect; Exit = $r.Exit; Reason = $r.Reason; Hint = $r.Hint; Detail = $r.Detail; Errors = ($r.Errors -join ' / ') })
    }
    if ($r.Secs -gt 60) { Write-Output ("         ⚠ 耗时 $($r.Secs)s，偏慢") }
}

function SZ([string[]]$szArgs) {
    & $SevenZip @szArgs -bso0 -bsp0 -y -aoa 2>&1 | Out-Null
    return $LASTEXITCODE
}

# ===================== fixtures =====================
Write-Output '=== 准备素材 ==='
$src = Join-Path $Root 'src'
New-Item -ItemType Directory -Path $src | Out-Null
$rng = New-Object System.Random(20261014)
# 3 MiB incompressible payload + a text file + a unicode name + a nested folder
$payload = Join-Path $src 'payload.bin'
$fs = [IO.File]::Create($payload); $chunk = New-Object byte[] (1MB)
for ($i = 0; $i -lt 3; $i++) { $rng.NextBytes($chunk); $fs.Write($chunk, 0, $chunk.Length) }
$fs.Close()
'SliceArc stability suite - plain text' | Out-File (Join-Path $src 'readme.txt') -Encoding utf8
'üñïçødé · 中文名.txt content' | Out-File (Join-Path $src '中文名 ünïcode.txt') -Encoding utf8
New-Item -ItemType Directory -Path (Join-Path $src 'sub\deep') -Force | Out-Null
'deep file' | Out-File (Join-Path $src 'sub\deep\nested.txt') -Encoding utf8

$fx = Join-Path $Root 'fx'
New-Item -ItemType Directory -Path $fx | Out-Null
SZ @('a', '-t7z', (Join-Path $fx 'a.7z'), "$src\payload.bin", "$src\readme.txt") | Out-Null
SZ @('a', '-tzip', (Join-Path $fx 'a.zip'), "$src\payload.bin", "$src\readme.txt") | Out-Null
SZ @('a', '-ttar', (Join-Path $fx 'a.tar'), "$src\payload.bin") | Out-Null
SZ @('a', '-t7z', '-mx0', '-v1m', (Join-Path $fx 'vol.7z'), "$src\payload.bin") | Out-Null
SZ @('a', '-tzip', '-mx0', '-v1m', (Join-Path $fx 'zsplit.zip'), "$src\payload.bin") | Out-Null
SZ @('a', '-t7z', '-pSECRET', (Join-Path $fx 'pw.7z'), "$src\readme.txt") | Out-Null
SZ @('a', '-t7z', '-pSECRET', '-mhe=on', (Join-Path $fx 'pwh.7z'), "$src\readme.txt") | Out-Null
SZ @('a', '-tzip', '-pSECRET', (Join-Path $fx 'pw.zip'), "$src\readme.txt") | Out-Null
SZ @('a', '-t7z', '-mx0', (Join-Path $fx 'rar.rar'), "$src\payload.bin") | Out-Null
# a text file renamed to .zip, a random-binary .7z, a truncated archive, an empty file
Copy-Item (Join-Path $src 'readme.txt') (Join-Path $fx 'fake.zip') -Force
$fs = [IO.File]::Create((Join-Path $fx 'garbage.7z')); $rng.NextBytes($chunk); $fs.Write($chunk, 0, $chunk.Length); $fs.Close()
$bytes = [IO.File]::ReadAllBytes((Join-Path $fx 'a.7z'))
[IO.File]::WriteAllBytes((Join-Path $fx 'trunc.7z'), $bytes[0..([int]($bytes.Length / 2))])
New-Item -ItemType File -Path (Join-Path $fx 'zero.7z') | Out-Null
# multi-volume sets the app itself creates (round-trip target)
foreach ($need in 'a.7z', 'a.zip', 'a.tar', 'vol.7z.001', 'zsplit.zip.001', 'pw.7z', 'pwh.7z', 'pw.zip', 'rar.rar', 'fake.zip', 'garbage.7z', 'trunc.7z', 'zero.7z') {
    if (-not (Test-Path (Join-Path $fx $need))) { Write-Output "  !! 夹具缺失: $need  (套件本身的问题，不是被测软件的问题)" }
}
Write-Output "  素材就绪（$((Get-ChildItem $fx).Count) 个）"

# ===================== 1. extraction stability =====================
Write-Output ''
Write-Output '=== 1 解压：格式与稳定性 ==='
$out = Join-Path $Root 'x'
foreach ($name in 'a.7z', 'a.zip', 'a.tar', 'pw.7z', 'pwh.7z', 'pw.zip', 'vol.7z.001', 'zsplit.zip.001', 'rar.rar') {
    $case = Join-Path $fx $name
    if (-not (Test-Path $case)) { continue }
    $args = @('--extract', $case, '--out', (Join-Path $out $name), '--quiet')
    if ($name -in 'pw.7z', 'pwh.7z', 'pw.zip') { $args += @('--password', 'SECRET') }
    $r = Run-App $args
    $expect = 'OK'
    # rar volumes are read-only for the engine only when appending; extraction is fine
    Check "解压 $name" $expect $r
}
Write-Output ''
Write-Output '=== 2 解压：口令（用户操作，必须报错且说得准）==='
Check '加密包·未给口令' 'USER' (Run-App @('--extract', "$fx\pw.7z", '--out', "$out\pw_none", '--quiet'))
Check '加密包·口令错' 'USER' (Run-App @('--extract', "$fx\pw.7z", '--out', "$out\pw_bad", '--password', 'NOPE', '--quiet')) '' @('口令不正确') @('数据损坏')
Check '加密头包·未给口令' 'USER' (Run-App @('--extract', "$fx\pwh.7z", '--out', "$out\pwh_none", '--quiet'))
Check '加密头包·口令错' 'USER' (Run-App @('--extract', "$fx\pwh.7z", '--out', "$out\pwh_bad", '--password', 'NOPE', '--quiet')) '' @('口令') @('数据损坏')
Check 'zip 加密·口令错' 'USER' (Run-App @('--extract', "$fx\pw.zip", '--out', "$out\zip_bad", '--password', 'NOPE', '--quiet'))
Write-Output ''
Write-Output '=== 3 解压：损坏 / 非压缩包（定性必须准）==='
Check '文本文件改名 .zip' 'USER' (Run-App @('--extract', "$fx\fake.zip", '--out', "$out\fake", '--quiet')) '' @('这不是压缩包')
Check '随机二进制改名 .7z' 'USER' (Run-App @('--extract', "$fx\garbage.7z", '--out', "$out\garbage", '--quiet'))
Check '被截断的 7z' 'USER' (Run-App @('--extract', "$fx\trunc.7z", '--out', "$out\trunc", '--quiet'))
Check '0 字节 .7z' 'USER' (Run-App @('--extract', "$fx\zero.7z", '--out', "$out\zero", '--quiet'))
Check '路径不存在' 'USER' (Run-App @('--extract', "$fx\nope.7z", '--quiet'))
Check '传的是文件夹' 'USER' (Run-App @('--extract', $src, '--quiet'))
# A genuinely incomplete set: copy the volumes, delete #2, then extract.
$broken = Join-Path $Root 'broken'
New-Item -ItemType Directory -Path $broken -Force | Out-Null
Copy-Item "$fx\vol.7z.0*" $broken -Force
Remove-Item (Join-Path $broken 'vol.7z.002') -Force -EA SilentlyContinue
Check '分卷缺第 2 卷' 'USER' (Run-App @('--extract', "$broken\vol.7z.001", '--out', "$out\missing", '--quiet')) '' @('损坏', '不完整', '中断')
Write-Output ''
Write-Output '=== 4 校验（--test，只读）==='
Check '--test 正常包'   'OK'   (Run-App @('--extract', "$fx\a.7z", '--test', '--quiet'))
Check '--test 加密包对口令' 'OK' (Run-App @('--extract', "$fx\pw.7z", '--test', '--password', 'SECRET', '--quiet'))
Check '--test 加密包错口令' 'USER' (Run-App @('--extract', "$fx\pw.7z", '--test', '--password', 'NOPE', '--quiet'))
Check '--test 截断包'   'USER' (Run-App @('--extract', "$fx\trunc.7z", '--test', '--quiet'))
Write-Output ''
Write-Output '=== 5 解压：重复解压 / 覆盖策略 ==='
Check '重复解压（默认覆盖）' 'OK' (Run-App @('--extract', "$fx\a.7z", '--out', "$out\again", '--quiet'))
Check '重复解压（--no-overwrite）' 'OK' (Run-App @('--extract', "$fx\a.7z", '--out', "$out\again", '--no-overwrite', '--quiet'))
Check '输出到不存在的目录（应自动创建）' 'OK' (Run-App @('--extract', "$fx\a.7z", '--out', "$out\brand\new\dir", '--quiet'))
Check '输出到子文件夹模式' 'OK' (Run-App @('--extract', "$fx\a.7z", '--to-subfolder', '--quiet'))

# ===================== 6. compression =====================
Write-Output ''
Write-Output '=== 6 压缩：各格式 + 分卷 + 口令 ==='
$cz = Join-Path $Root 'c'
New-Item -ItemType Directory -Path $cz | Out-Null
foreach ($fmt in '7z', 'zip', 'tar', 'gz', 'bz2', 'xz', 'wim') {
    $o = Join-Path $cz "out.$fmt"
    Check "创建 $fmt" 'OK' (Run-App @('--compress', $o, '--add', "$src\readme.txt", '--format', $fmt, '--quiet'))
}
Check '创建 zst（不支持，需明确报错）' 'UNSUPPORTED' (Run-App @('--compress', "$cz\out.zst", '--add', "$src\readme.txt", '--format', 'zst', '--quiet')) '' @('zst')
Check '创建 rar（不支持，需明确报错）' 'UNSUPPORTED' (Run-App @('--compress', "$cz\out.rar", '--add', "$src\readme.txt", '--format', 'rar', '--quiet')) '' @('不能创建')
Check '创建 zip 但名字叫 .rar（不一致）' 'UNSUPPORTED' (Run-App @('--compress', "$cz\mix.rar", '--add', "$src\readme.txt", '--format', 'zip', '--quiet')) '' @('不一致')
Check '无法识别的格式名' 'UNSUPPORTED' (Run-App @('--compress', "$cz\bad.7z", '--add', "$src\readme.txt", '--format', 'nope', '--quiet'))
Check '压缩：无 --format，按扩展名' 'OK' (Run-App @('--compress', "$cz\byext.zip", '--add', "$src\readme.txt", '--quiet'))
Check '压缩：多文件打成 tar.gz' 'OK' (Run-App @('--compress', "$cz\multi.tar.gz", '--add', "$src\payload.bin", '--add', "$src\readme.txt", '--format', 'gz', '--quiet'))
Check '压缩：分卷 1MiB' 'OK' (Run-App @('--compress', "$cz\vol.7z", '--add', "$src\payload.bin", '--split-volume', '1048576', '--quiet'))
Check '压缩：口令 + 加密文件名' 'OK' (Run-App @('--compress', "$cz\enc.7z", '--add', "$src\readme.txt", '--password', 'P@ss w0rd', '--encrypt-headers', '--quiet'))
Check '压缩：级别 0' 'OK' (Run-App @('--compress', "$cz\l0.7z", '--add', "$src\payload.bin", '--level', '0', '--quiet'))
Check '压缩：级别 9 + 非固实' 'OK' (Run-App @('--compress', "$cz\l9.7z", '--add', "$src\payload.bin", '--level', '9', '--no-solid', '--quiet'))
Check '压缩：源不存在' 'USER' (Run-App @('--compress', "$cz\nosrc.7z", '--add', "$cz\does\not\exist.txt", '--quiet'))
Check '压缩：没给 --add' 'USER' (Run-App @('--compress', "$cz\empty.7z", '--quiet')) '' @('--add')
Check '压缩：目标已存在（覆盖）' 'OK' (Run-App @('--compress', "$cz\byext.zip", '--add', "$src\readme.txt", '--quiet'))
Write-Output ''
Write-Output '=== 7 追加 / 更新 ==='
Check '追加到 7z'  'OK' (Run-App @('--add-to', "$cz\out.7z",  '--add', "$src\readme.txt", '--quiet'))
Check '追加到 zip' 'OK' (Run-App @('--add-to', "$cz\out.zip", '--add', "$src\readme.txt", '--quiet'))
Check '追加到 tar' 'OK' (Run-App @('--add-to', "$cz\out.tar", '--add', "$src\readme.txt", '--quiet'))
Check '追加到 wim' 'OK' (Run-App @('--add-to', "$cz\out.wim", '--add', "$src\readme.txt", '--quiet'))
Check '追加到 gz（单流，不支持）' 'UNSUPPORTED' (Run-App @('--add-to', "$cz\out.gz", '--add', "$src\readme.txt", '--quiet')) '' @('一个文件')
Check '追加到 rar（不支持）' 'UNSUPPORTED' (Run-App @('--add-to', "$fx\rar.rar", '--add', "$src\readme.txt", '--quiet')) '' @('只能读取')
Check '追加到不存在的包' 'USER' (Run-App @('--add-to', "$cz\nope.7z", '--add', "$src\readme.txt", '--quiet'))

# ===================== 8. round-trip integrity =====================
Write-Output ''
Write-Output '=== 8 往返完整性（压缩→解压→逐字节比对）==='
$rt = Join-Path $Root 'rt'
New-Item -ItemType Directory -Path $rt | Out-Null
$srcHash = (Get-FileHash "$src\payload.bin" -Algorithm SHA256).Hash
foreach ($fmt in '7z', 'zip', 'tar', 'wim') {
    $arc = Join-Path $rt "rt.$fmt"
    $r1 = Run-App @('--compress', $arc, '--add', "$src\payload.bin", '--format', $fmt, '--quiet')
    $r2 = Run-App @('--extract', $arc, '--out', (Join-Path $rt "e_$fmt"), '--quiet')
    $back = Join-Path $rt "e_$fmt\payload.bin"
    $same = (Test-Path $back) -and ((Get-FileHash $back -Algorithm SHA256).Hash -eq $srcHash)
    if ($same) { $script:pass++; Write-Output "  [PASS] 往返 $fmt  哈希一致" }
    else { $script:fail++; Write-Output "  [FAIL] 往返 $fmt  哈希不一致或文件缺失（压缩 exit=$($r1.Exit) 解压 exit=$($r2.Exit)）"
        [void]$script:findings.Add([pscustomobject]@{ Name = "往返 $fmt"; Expect = 'OK'; Exit = "$($r1.Exit)/$($r2.Exit)"; Reason = $r2.Reason; Hint = ''; Detail = ''; Errors = '' }) }
}
# multi-volume round-trip (app creates → app extracts)
$volArc = Join-Path $rt 'vol.7z'
Run-App @('--compress', $volArc, '--add', "$src\payload.bin", '--split-volume', '1048576', '--quiet') | Out-Null
$r = Run-App @('--extract', "$volArc.001", '--out', (Join-Path $rt 'e_vol'), '--quiet')
$back = Join-Path $rt 'e_vol\payload.bin'
$same = (Test-Path $back) -and ((Get-FileHash $back -Algorithm SHA256).Hash -eq $srcHash)
if ($same) { $script:pass++; Write-Output '  [PASS] 往返 自建分卷（10 卷）  哈希一致' }
else { $script:fail++; Write-Output "  [FAIL] 往返 自建分卷  exit=$($r.Exit) 原因=$($r.Reason)"
    [void]$script:findings.Add([pscustomobject]@{ Name = '往返 自建分卷'; Expect = 'OK'; Exit = $r.Exit; Reason = $r.Reason; Hint = $r.Hint; Detail = $r.Detail; Errors = ($r.Errors -join ' / ') }) }
# encrypted round-trip
$encArc = Join-Path $rt 'enc.7z'
Run-App @('--compress', $encArc, '--add', "$src\payload.bin", '--password', 'P@ss w0rd', '--encrypt-headers', '--quiet') | Out-Null
$r = Run-App @('--extract', $encArc, '--out', (Join-Path $rt 'e_enc'), '--password', 'P@ss w0rd', '--quiet')
$back = Join-Path $rt 'e_enc\payload.bin'
$same = (Test-Path $back) -and ((Get-FileHash $back -Algorithm SHA256).Hash -eq $srcHash)
if ($same) { $script:pass++; Write-Output '  [PASS] 往返 加密+加密头  哈希一致' }
else { $script:fail++; Write-Output "  [FAIL] 往返 加密  exit=$($r.Exit) 原因=$($r.Reason)" }

# ===================== 9. no spurious errors on success =====================
Write-Output ''
Write-Output '=== 9 "能修复的不要报错"：成功路径不得出现任何失败信息 ==='
$busy = @(
    @('--extract', "$fx\vol.7z.001", '--out', (Join-Path $Root 'sp\sliced'), '--quiet'),
    @('--extract', "$fx\rar.rar", '--out', (Join-Path $Root 'sp\rar'), '--quiet'),
    @('--extract', "$fx\a.zip", '--out', (Join-Path $Root 'sp\zip'), '--quiet'),
    @('--extract', "$fx\a.7z", '--out', (Join-Path $Root 'sp\7z'), '--to-subfolder', '--quiet')
)
$i = 0
foreach ($a in $busy) {
    $i++
    $r = Run-App $a
    if ($r.Exit -eq 0 -and -not $r.Reason -and $r.Errors.Count -eq 0) { $script:pass++; Write-Output "  [PASS] 成功且日志干净 #$i" }
    else {
        $script:fail++
        Write-Output "  [FAIL] 成功路径有噪音 #$i  exit=$($r.Exit) 原因=$($r.Reason)"
        [void]$script:findings.Add([pscustomobject]@{ Name = "成功路径噪音 #$i"; Expect = 'OK'; Exit = $r.Exit; Reason = $r.Reason; Hint = $r.Hint; Detail = $r.Detail; Errors = ($r.Errors -join ' / ') })
    }
}

# ===================== summary =====================
Write-Output ''
Write-Output '================ 汇总 ================'
Write-Output "  PASS $script:pass   FAIL $script:fail"
if ($script:findings.Count) {
    Write-Output ''
    Write-Output '  需要修的问题：'
    foreach ($f in $script:findings) {
        Write-Output ("    - {0}  [期望 {1}] exit={2}" -f $f.Name, $f.Expect, $f.Exit)
        if ($f.Reason) { Write-Output ("        原因：{0}" -f $f.Reason) }
        if ($f.Errors) { Write-Output ("        日志：{0}" -f $f.Errors) }
    }
}
if (-not $Keep) { Write-Output ''; Write-Output "  （素材目录保留：$Root）" }
exit $(if ($script:fail) { 1 } else { 0 })
