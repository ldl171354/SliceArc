# drag-cli-e2e.ps1 — 端到端验证「拖出解压」的取数机制（不经鼠标）
# 关键：拖出与 --path 解压共用同一条 OutputFactory 管线（ArchiveEntryReader 也走它），
# 所以用 CLI 精确复现拖出的取数形态，即可校验"字节正确 + 零临时文件"这两点；
# 描述符偏移已由真 Explorer 实测通过（拖 note.txt 落盘成功）。
$ErrorActionPreference = 'Continue'
$App = 'D:\SliceArc\dist\app\SliceArc.App.exe'
$root = 'D:\SliceArc\artifacts\drag-shapes'
$src = "$root\src"
$Arc = "$root\shapes.7z"

Write-Output '=== 0. 确认拖出确实成功过一次（上一轮真 Explorer 的结果）==='
Get-Content "$root\..\..\dist\app\SliceArc.log" -EA SilentlyContinue |
  Select-String 'FileContents|effect=' | Select-Object -Last 3 | ForEach-Object { "  " + ($_.Line -replace '^(\S+ \S+) \[INFO \] ','') }

Write-Output ''
Write-Output '=== 1. 拖出用的路径形态（包内是相对路径）==='
& 'C:\Program Files\7-Zip\7z.exe' l $Arc -bso0 | Select-String 'A ' | ForEach-Object { "  $($_.Line.Trim() -replace '\s{2,}',' ')" }

Write-Output ''
function Drop($l) { ($l | Where-Object { $_ -notmatch '^\d{4}-\d{2}-\d{2} ' -and $_ -notmatch '^#< CLIXML' -and $_ -notmatch '^<Objs' }) }
function HashOf($f) { if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash.Substring(0,16) } else { '(缺)' } }

Write-Output '=== 2. 按拖出的三种形态取数（相对名 / 子目录 / 中文名）==='
$out = "$root\cli-out"
Remove-Item $out -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory $out | Out-Null
$prev = Drop(@(Get-Content 'D:\SliceArc\dist\app\SliceArc.log' -EA SilentlyContinue)).Count

foreach ($t in @('note.txt','dir/inner.txt','中文说明.md')) {
  $a = @('--extract', $Arc, '--path', $t, '--out', "`"$out`"", '--quiet')
  $p = Start-Process -FilePath $App -ArgumentList $a -Wait -PassThru
  Write-Output "  --path '$t' exit=$($p.ExitCode)"
}

$new = Drop(@(Get-Content 'D:\SliceArc\dist\app\SliceArc.log' -EA SilentlyContinue))
$new[$prev..($new.Count-1)] | Select-String '原因：|建议：|\[ERROR\]' | Select-Object -First 4 | ForEach-Object { "    $_" }

Write-Output ''
Write-Output '=== 3. 字节是否与原始一致（决定拖出内容对不对）==='
foreach ($pair in @(@('note.txt','note.txt'), @('inner.txt','dir\inner.txt'), @('中文说明.md','中文说明.md'))) {
  $a = HashOf (Join-Path $src $pair[1])
  $b = HashOf (Join-Path $out $pair[0])
  Write-Output ("  {0,-14} 原始={1} 拖出={2}  {3}" -f $pair[0], $a, $b, $(if ($a -eq $b -and $a -ne '(缺)') {'✅'} else {'❌'}))
}
Write-Output ''
Write-Output '=== 4. 临时文件计数（拖拽期间不得新增）==='
foreach ($d in 'C:\Users\17135\AppData\Local\Temp','C:\Users\17135\AppData\Local\Temp\SliceArc') {
  if (Test-Path $d) { "  $d : $((Get-ChildItem $d -Recurse -File -EA SilentlyContinue | Measure-Object).Count) 个文件" }
}
Write-Output ''
Write-Output '=== 5. 大文件走一次（3MB 单流，验缓冲区/背压）==='
$p = Start-Process -FilePath $App -ArgumentList @('--extract', $Arc, '--path', "`"$src\big.bin`"", '--out', "`"$root\cli-big`"", '--quiet') -Wait -PassThru
Write-Output "  exit=$($p.ExitCode)"
$g = Get-ChildItem "$root\cli-big" -Recurse -File -EA SilentlyContinue
if ($g) { $g | ForEach-Object { "  得到 $($_.Name) $($_.Length) B  $((Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0,16))" } }
"  原始 big.bin $((Get-FileHash "$src\big.bin" -Algorithm SHA256).Hash.Substring(0,16))"
