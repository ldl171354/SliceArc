using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SliceArc.Core.DragDrop;
using SliceArc.Core.Extraction;
using SliceArc.Core.SevenZip;
using SliceArc.Core.Shell;
using SliceArc.Core.Volumes;
using SliceArc.Shared;
using SliceArc.Probe;

Console.OutputEncoding = Encoding.UTF8;

const string SevenZipExe = @"C:\Program Files\7-Zip\7z.exe";

// On D: next to the repo, NOT %TEMP%: this machine's C: is the tight one (the owner
// keeps everything off it on purpose) and the probe creates ~60 MB of archives per
// run. SLICEARC_PROBE_WORK overrides when someone wants it elsewhere.
var work = Environment.GetEnvironmentVariable("SLICEARC_PROBE_WORK");
if (string.IsNullOrWhiteSpace(work))
{
    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    work = Path.Combine(repoRoot, "artifacts", "probe-work");
}

var failures = 0;
void Check(string label, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail is null ? "" : "  -> " + detail)}");
    if (!ok) failures++;
}

if (Directory.Exists(work)) Directory.Delete(work, true);
Directory.CreateDirectory(work);

// ===================================================================== 1
Console.WriteLine("=== 1. 7z.dll 加载与格式枚举 ===");
var formats = SevenZipLibrary.Formats;
Check("7z.dll 已解析", SevenZipLibrary.LoadedFrom is not null, SevenZipLibrary.LoadedFrom);
Check("格式数量合理", formats.Count > 10, $"{formats.Count} 个");
foreach (var f in formats.Take(8)) Console.WriteLine($"      {f.Index,3}  {f}");
Console.WriteLine($"      ... 其余 {Math.Max(0, formats.Count - 8)} 个略");

// ===================================================================== 2
Console.WriteLine();
Console.WriteLine("=== 2. 构造分卷测试包 ===");
var payload = new byte[3 * 1024 * 1024];
new Random(20261012).NextBytes(payload);          // incompressible on purpose
var srcFile = Path.Combine(work, "payload.bin");
File.WriteAllBytes(srcFile, payload);
var srcHash = Convert.ToHexString(SHA256.HashData(payload));

var arcPath = Path.Combine(work, "sample.7z");
RunSevenZip($"a -t7z -mx1 -v1m \"{arcPath}\" \"{srcFile}\"");

var produced = Directory.GetFiles(work, "sample.7z.*").OrderBy(p => p, StringComparer.Ordinal).ToList();
Check("生成了多个分卷", produced.Count > 1, $"{produced.Count} 卷");
foreach (var p in produced)
    Console.WriteLine($"      {Path.GetFileName(p),-20} {new FileInfo(p).Length,12:N0} B");

// ===================================================================== 3
Console.WriteLine();
Console.WriteLine("=== 3. 分卷组识别 ===");
var group = VolumeDetector.DetectGroup(produced[0]);
Check("识别为分卷组", group is not null);
if (group is null) return failures;

Check("风格判定正确", group.Style == VolumeStyle.NumericSuffix, group.Style.ToString());
Check("卷数一致", group.Volumes.Count == produced.Count, $"{group.Volumes.Count}");
Check("序号连续完整", group.IsComplete);
Check("总字节数一致",
    group.TotalBytes == produced.Sum(p => new FileInfo(p).Length),
    $"{group.TotalBytes:N0} B");

foreach (var v in group.Volumes)
    Console.WriteLine($"      [{v.Index}] {v.FileName,-20} off={v.ArchiveOffset,12:N0}  end={v.ArchiveEnd,12:N0}");

// ===================================================================== 4
Console.WriteLine();
Console.WriteLine("=== 4. VolumeAwareStream 打开归档（CCW 关键验证）===");
using var stream = new VolumeAwareStream(group);

var fmt = SevenZipLibrary.FindByExtension(group.BaseName);
Check("按扩展名匹配到格式处理器", fmt is not null, fmt?.Name ?? "(none)");
if (fmt is null) return failures;

IInArchive? archive = null;
uint itemCount = 0;
try
{
    archive = SevenZipLibrary.CreateInArchive(fmt.Clsid);
    Check("CreateObject -> IInArchive", archive is not null);

    // 7z.dll must now call back into OUR IInStream. Hand over the explicit
    // IInStream pointer: the default marshaler would pass IUnknown, and 7-Zip
    // calls Read/Seek straight off the pointer without a QueryInterface.
    var streamPtr = Marshal.GetComInterfaceForObject(stream, typeof(IInStream));
    Check("取到 IInStream 接口指针", streamPtr != IntPtr.Zero, $"0x{streamPtr:X}");

    // 7-Zip's own clients always pass a non-NULL search limit here; some
    // handlers are stricter about it than the header comment implies.
    var maxCheck = Marshal.AllocHGlobal(8);
    Marshal.WriteInt64(maxCheck, 1L << 23);

    int hr;
    try
    {
        hr = archive!.Open(streamPtr, maxCheck, IntPtr.Zero);
    }
    finally
    {
        Marshal.FreeHGlobal(maxCheck);
        Marshal.Release(streamPtr);
    }
    Check("IInArchive::Open 成功", hr == 0, $"HRESULT=0x{hr:X8}");
    if (hr != 0) return failures;

    Check("7z.dll 确实读取了我们的流", stream.BytesRead > 0, $"{stream.BytesRead:N0} B");

    archive.GetNumberOfItems(out itemCount);
    Check("枚举到条目", itemCount > 0, $"{itemCount} 项");

    // ---- item properties -------------------------------------------------
    Console.WriteLine("      条目明细:");
    for (uint i = 0; i < itemCount; i++)
    {
        var path = GetString(archive, i, SevenZipNative.PropId.Path);
        var size = GetUInt64(archive, i, SevenZipNative.PropId.Size);
        var packed = GetUInt64(archive, i, SevenZipNative.PropId.PackSize);
        var solid = GetUInt64(archive, i, SevenZipNative.PropId.Solid);
        var isDir = GetUInt64(archive, i, SevenZipNative.PropId.IsDir);
        Console.WriteLine(
            $"        [{i}] {path}  size={size:N0} packed={packed:N0} solid={solid} dir={isDir}");
    }

    // ---- archive-level properties ---------------------------------------
    var numVolumes = GetArchiveUInt64(archive, SevenZipNative.PropId.NumVolumes);
    var phySize = GetArchiveUInt64(archive, SevenZipNative.PropId.PhySize);
    Console.WriteLine($"      归档属性: NumVolumes={numVolumes}  PhySize={phySize:N0}");

    // ================================================================= 5
    Console.WriteLine();
    Console.WriteLine("=== 5. 流式解压 + 分卷消费追踪 ===");

    // Open() seeks to the archive tail to find the header, so the accounting
    // only becomes meaningful once extraction starts.
    stream.BeginExtractionTracking();
    Check("Open 后重置追踪，回退计数归零", stream.BackSeekCount == 0);

    var outDir = Path.Combine(work, "out");
    Directory.CreateDirectory(outDir);

    var sink = new ExtractSink(archive, outDir, stream);
    var sinkPtr = Marshal.GetComInterfaceForObject(sink, typeof(IArchiveExtractCallback));
    int hr2;
    try
    {
        hr2 = archive.Extract(null, uint.MaxValue, 0, sinkPtr);
    }
    finally
    {
        Marshal.Release(sinkPtr);
    }
    Check("IInArchive::Extract 成功", hr2 == 0, $"HRESULT=0x{hr2:X8}");

    // 7-Zip seeks back to the data region at the start of Extract, so a rewind
    // here is expected behaviour, not a failure. What matters is that the merged
    // coverage set reaches the end of the archive stream.
    Console.WriteLine($"      backSeeks={stream.BackSeekCount}（回退到数据起点属正常）");
    Check("覆盖集已推进到 EOF", stream.CoveredEnd >= stream.TotalBytes,
        $"covered={stream.CoveredEnd:N0}/{stream.TotalBytes:N0}");

    Console.WriteLine($"      归档流已读: {stream.HighWaterMark:N0} / {stream.TotalBytes:N0} B");
    Console.WriteLine("      分卷消费判定:");
    var spent = 0;
    for (var i = 0; i < group.Volumes.Count; i++)
    {
        var isSpent = stream.IsVolumeSpent(i);
        if (isSpent) spent++;
        Console.WriteLine(
            $"        [{i}] {group.Volumes[i].FileName,-20} " +
            $"read={stream.VolumeBytesRead(i),12:N0}/{group.Volumes[i].Length,12:N0}  " +
            $"spent={isSpent}");
    }
    Check("至少最后一个分卷被判定为可删", spent >= 1, $"{spent}/{group.Volumes.Count} 卷");

    // ---- verify the extracted bytes -------------------------------------
    var extracted = Path.Combine(outDir, "payload.bin");
    Check("解压产物存在", File.Exists(extracted));
    if (File.Exists(extracted))
    {
        var outHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(extracted)));
        Check("解压内容与原始一致 (SHA256)", outHash == srcHash, outHash[..16] + "...");
    }

    archive.Close();
}
catch (Exception ex)
{
    Check("未抛出异常", false, ex.GetType().Name + ": " + ex.Message);
    Console.WriteLine(ex);
}
finally
{
    if (archive is not null) SevenZipLibrary.ReleaseInArchive(archive);
}

// ===================================================================== 6
Console.WriteLine();
Console.WriteLine("=== 6. ExtractionEngine 端到端（产品化路径）===");

// Section 6 deletes volumes on purpose, so it works on its own copy.
var engineDir = Path.Combine(work, "engine");
Directory.CreateDirectory(engineDir);
foreach (var p in produced)
    File.Copy(p, Path.Combine(engineDir, Path.GetFileName(p)), overwrite: true);

var engineVolumes = Directory.GetFiles(engineDir, "sample.7z.*")
    .OrderBy(p => p, StringComparer.Ordinal).ToList();
var engineArc = engineVolumes[0];

// ---- 6.1 open + enumerate ----
using (var engine = new ExtractionEngine(engineArc))
{
    engine.Open();
    Check("6.1 分卷组识别", engine.IsMultiVolume && engine.VolumeCount == produced.Count,
        $"{engine.VolumeCount} 卷 / {engine.Format?.Name}");
    Check("6.1 条目数一致", (uint)engine.Entries.Count == itemCount, $"{engine.Entries.Count} 项");
    var withTime = engine.Entries.FirstOrDefault(e => e.Modified is not null);
    Check("6.1 修改时间已读出", withTime is not null, withTime?.Modified?.ToString("u") ?? "(none)");
}

// ---- 6.2 full extraction ----
var fullOut = Path.Combine(work, "engine-out");
if (Directory.Exists(fullOut)) Directory.Delete(fullOut, true);

var progressLog = new List<ExtractionProgress>();
ExtractionResult fullResult;
using (var engine = new ExtractionEngine(engineArc))
{
    fullResult = engine.Extract(
        new ExtractionOptions { OutputDirectory = fullOut },
        progressLog.Add);
}

Check("6.2 全量解压成功", fullResult.Ok, $"{fullResult.Extracted} 项 / {fullResult.BytesWritten:N0} B");
var engineOutFile = Path.Combine(fullOut, "payload.bin");
Check("6.2 产物存在", File.Exists(engineOutFile));
if (File.Exists(engineOutFile))
{
    var outHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engineOutFile)));
    Check("6.2 SHA256 与原始一致", outHash == srcHash, outHash[..16] + "...");
}
Check("6.2 进度回调单调递增", progressLog.Count > 0 && IsMonotonic(progressLog), $"{progressLog.Count} 次回调");
Check("6.2 进度收敛到 100%",
    progressLog.Count > 0 && progressLog[^1].BytesDone == progressLog[^1].BytesTotal,
    progressLog.Count > 0 ? $"{progressLog[^1].BytesDone:N0}/{progressLog[^1].BytesTotal:N0}" : "(无回调)");

// ---- 6.3 selection ----
var selectOut = Path.Combine(work, "engine-select");
using (var engine = new ExtractionEngine(engineArc))
{
    var r = engine.Extract(new ExtractionOptions
    {
        OutputDirectory = selectOut,
        IncludePaths = new[] { "payload.bin" },
    });
    Check("6.3 按精确路径选择", r.Ok && r.Extracted == 1 && File.Exists(Path.Combine(selectOut, "payload.bin")),
        $"{r.Extracted} 项");
}

var noneOut = Path.Combine(work, "engine-none");
using (var engine = new ExtractionEngine(engineArc))
{
    var r = engine.Extract(new ExtractionOptions
    {
        OutputDirectory = noneOut,
        IncludeGlobs = new[] { "no-such-*.bin" },
    });
    Check("6.3 通配符未命中时什么都不解", r.Ok && r.Extracted == 0 && !File.Exists(Path.Combine(noneOut, "payload.bin")),
        $"{r.Extracted} 项");
}

// ---- 6.4 overwrite=false ----
using (var engine = new ExtractionEngine(engineArc))
{
    var r = engine.Extract(new ExtractionOptions { OutputDirectory = fullOut, Overwrite = false });
    Check("6.4 不覆盖时全部跳过", r.Ok && r.Extracted == 0 && r.Skipped == 1, $"skipped={r.Skipped}");
}

// ---- 6.5 test mode ----
using (var engine = new ExtractionEngine(engineArc))
{
    var r = engine.Test();
    Check("6.5 校验模式通过且不落盘", r.Ok && r.Tested == 1 && r.Extracted == 0 && r.BytesWritten == 0,
        $"tested={r.Tested} written={r.BytesWritten}");
}

// ---- 6.6 path traversal ----
var evilZip = Path.Combine(work, "evil.zip");
using (var zip = new ZipArchive(File.Create(evilZip), ZipArchiveMode.Create))
{
    // ZipArchive refuses a second entry while the first one's stream is open,
    // so each entry has to be written and closed in turn.
    using (var a = new StreamWriter(zip.CreateEntry(@"..\..\evil.txt").Open()))
    {
        a.Write("pwned");
    }
    using (var b = new StreamWriter(zip.CreateEntry("../outside2.txt").Open()))
    {
        b.Write("pwned");
    }
}

var evilOut = Path.Combine(work, "evil-out");
try
{
    using var evilEngine = new ExtractionEngine(evilZip);
    var r = evilEngine.Extract(new ExtractionOptions { OutputDirectory = evilOut });
    Check("6.6 越界条目被拒绝", r.Failed >= 1 && r.Errors.Any(err => err.Contains("越界")),
        string.Join(" | ", r.Errors));
}
catch (Exception ex)
{
    // Open() must never turn an unreadable file into an uncaught throw: the
    // caller (and the archive-check tool) needs a failure it can show, so an
    // escape here is itself a defect and is recorded as one.
    Check("6.6 越界条目被拒绝", false, ex.GetType().Name + ": " + ex.Message);
}
Check("6.6 没有文件写到输出目录之外",
    !File.Exists(Path.Combine(work, "evil.txt")) && !File.Exists(Path.Combine(work, "outside2.txt")));

// ---- 6.7 delete spent volumes ----
var delDir = Path.Combine(work, "engine-del");
Directory.CreateDirectory(delDir);
foreach (var p in produced)
    File.Copy(p, Path.Combine(delDir, Path.GetFileName(p)), overwrite: true);

var delVolumes = Directory.GetFiles(delDir, "sample.7z.*")
    .OrderBy(p => p, StringComparer.Ordinal).ToList();
var delOut = Path.Combine(work, "engine-del-out");

using (var engine = new ExtractionEngine(delVolumes[0]))
{
    var r = engine.Extract(new ExtractionOptions
    {
        OutputDirectory = delOut,
        DeleteSpentVolumes = true,
        KeepLastVolumes = 1,
    });

    Check("6.7 解压成功", r.Ok, $"{r.Extracted} 项");
    Check("6.7 删除卷数 = 总数 - 保险丝", r.DeletedVolumes.Count == delVolumes.Count - 1,
        $"{r.DeletedVolumes.Count}/{delVolumes.Count} 卷");
    Check("6.7 回收字节数 > 0", r.BytesFreed > 0, $"{r.BytesFreed:N0} B");
    Check("6.7 最后一卷仍在（保险丝生效）", File.Exists(delVolumes[^1]));
    Check("6.7 前 N-1 卷已从磁盘消失",
        delVolumes.Take(delVolumes.Count - 1).All(p => !File.Exists(p)));

    var delOutFile = Path.Combine(delOut, "payload.bin");
    Check("6.7 边删边解的产物仍然正确",
        File.Exists(delOutFile) &&
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(delOutFile))) == srcHash);
}

// ---- 6.8 archive scanner ----
var scanned = ArchiveScanner.Scan(engineDir);
Check("6.8 目录扫描把 4 卷折叠成 1 个归档",
    scanned.Count == 1 && scanned[0].VolumeCount == produced.Count,
    scanned.Count == 1 ? $"{scanned[0].DisplayName} / {scanned[0].VolumeCount} 卷" : $"{scanned.Count} 项");

// ---- 6.9 junk file ----
var junk = Path.Combine(work, "not-an-archive.bin");
File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
var rejected = false;
try
{
    using var junkEngine = new ExtractionEngine(junk);
    junkEngine.Open();
}
catch (InvalidDataException)
{
    rejected = true;
}
Check("6.9 非归档文件被明确拒绝", rejected);

// ===================================================================== 7
Console.WriteLine();
Console.WriteLine("=== 7. 右键菜单注册表往返（隔离在 HKCU 测试项）===");

const string scratch = @"Software\SliceArc.Probe\Classes";
ShellMenuRegistrar.Unregister(scratch);
ShellMenuRegistrar.Register(
    new ShellMenuOptions { ExecutablePath = @"C:\Windows\System32\notepad.exe" },
    scratch);

Check("7.1 注册后可读回", ShellMenuRegistrar.IsRegistered(scratch));

var menuLines = ShellMenuRegistrar.Inspect(scratch);
Check("7.2 文件动词齐全（平铺，每个都是顶层项）",
    new[] { "01_here", "02_sub", "03_here_delete", "10_open", "20_zip", "21_7z", "22_split" }
        .All(k => menuLines.Any(l => l.Contains(k))),
    $"{menuLines.Count} 行");
Check("7.3 命令契约正确",
    menuLines.Any(l => l.Contains("--extract \"%1\" --here")) &&
    menuLines.Any(l => l.Contains("--delete-spent")) &&
    menuLines.Any(l => l.Contains("--compress --add \"%1\" --format zip")) &&
    menuLines.Any(l => l.Contains("--compress --add \"%1\" --format 7z")) &&
    menuLines.Any(l => l.Contains("--split-volume 52428800")),
    menuLines.FirstOrDefault(l => l.Contains("--compress")));

// Every verb must carry its own command: a verb without one is the dead-item
// failure mode the cascade form produced.
var verbsWithoutCommand = menuLines.Where(l => l.Contains("= ") && l.Contains("-> (无命令)")).ToList();
Check("7.4 每个动词都自带 command（没有空壳项）", verbsWithoutCommand.Count == 0,
    verbsWithoutCommand.Count == 0 ? "全部有命令" : string.Join(" | ", verbsWithoutCommand));

Check("7.5 文件夹与背景动词也在",
    menuLines.Any(l => l.Contains(@"Directory\shell")) &&
    menuLines.Any(l => l.Contains(@"Directory\Background\shell")));

ShellMenuRegistrar.Unregister(scratch);
var leftovers = new[]
    {
        @"*\shell\SliceArc01_here",
        @"*\shell\SliceArc02_sub",
        @"*\shell\SliceArc03_here_delete",
        @"*\shell\SliceArc10_open",
        @"*\shell\SliceArc20_zip",
        @"*\shell\SliceArc21_7z",
        @"*\shell\SliceArc22_split",
    }
    .Where(verb => Registry.CurrentUser.OpenSubKey(scratch + "\\" + verb) is not null)
    .ToList();
Check("7.6 注销后动词键全部消失",
    !ShellMenuRegistrar.IsRegistered(scratch) && leftovers.Count == 0,
    leftovers.Count == 0 ? "干净" : string.Join(" | ", leftovers));

// The probe owns this scratch namespace; the verb roots above are shared with
// the real install, so only the probe's own scaffolding is removed here.
Registry.CurrentUser.DeleteSubKeyTree(@"Software\SliceArc.Probe", throwOnMissingSubKey: false);
Check("7.7 测试脚手架已清理",
    Registry.CurrentUser.OpenSubKey(@"Software\SliceArc.Probe") is null);

// ===================================================================== 8
Console.WriteLine();
Console.WriteLine("=== 8. 资源管理器是否真的会显示（只读探测，需已注册）===");

if (!ShellMenuRegistrar.IsRegistered())
{
    Console.WriteLine("  跳过：当前用户还没注册右键菜单（先执行 SliceArc.App.exe --register）");
}
else
{
    var onArchive = ShellMenuQuery.Entries(engineVolumes[0]);
    var onPlain = ShellMenuQuery.Entries(junk);

    // The verbs are flat now: each action is its own top-level menu item. The
    // earlier cascade form rendered an empty submenu, which is exactly the bug
    // these assertions exist to catch - so every expected command must appear
    // directly in the top-level menu, not inside a submenu.
    var wanted = new[]
    {
        "超速解压缩：解压到当前文件夹",
        "超速解压缩：解压到「归档名」文件夹",
        "超速解压缩：解压并删除已用完的分卷",
        "超速解压缩：测试压缩包完整性",
        "用超速解压缩打开",
    };

    var flat = onArchive.Where(e => e.Depth == 0).ToList();
    Check("8.1 压缩包上能看到本程序的命令组",
        flat.Any(entry => entry.Text.Contains("超速解压缩")),
        string.Join(" | ", flat.Select(entry => entry.Text)));

    var missing = wanted.Where(w => !flat.Any(entry => entry.Text.Contains(w))).ToList();
    Check("8.2 四个解压命令都在顶层菜单里（不是空壳子菜单）",
        missing.Count == 0,
        missing.Count == 0 ? $"找到 {wanted.Length} 项" : string.Join(" | ", missing));

    // Two verb families share the "超速解压缩" prefix: the extract/test verbs are
    // restricted to archive extensions via AppliesTo, while the compress verbs
    // deliberately apply to any file. They must be checked separately - a single
    // "does the prefix appear" test would report the intended behaviour as a bug.
    var plainExtract = onPlain.Any(entry => entry.Text.Contains("解压到当前文件夹"));
    var plainCompress = onPlain.Any(entry => entry.Text.Contains("压缩到"));
    Console.WriteLine($"  普通文件（非压缩包）上的菜单：" +
                      $"解压动词={(plainExtract ? "出现" : "不出现")}（应为不出现），" +
                      $"压缩动词={(plainCompress ? "出现" : "不出现")}（应为出现）");
    Check("8.3 AppliesTo 只放行压缩包，压缩动词对所有文件可见",
        !plainExtract && plainCompress, $"extract={plainExtract} compress={plainCompress}");

    // 8.4 A self-extracting first volume ends in .exe, so an AppliesTo that is
    // only an extension list hides every extract verb for it - which is exactly
    // what a user right-clicking Build.part01.exe ran into. The pattern, not the
    // extension, is what makes it an archive.
    var sfxSample = Path.Combine(work, "Build.25778117.part01.exe");
    File.WriteAllBytes(sfxSample, new byte[] { 0 });
    var onSfx = ShellMenuQuery.Entries(sfxSample);
    var sfxHere = onSfx.Any(e => e.Text.Contains("解压到当前文件夹"));
    var sfxDelete = onSfx.Any(e => e.Text.Contains("删除已用完"));
    Check("8.4 .part01.exe 上出现解压动词，含「解压并删除已用完的分卷」",
        sfxHere && sfxDelete,
        $"解压到当前文件夹={sfxHere} 边解边删={sfxDelete}；实到动词: " +
        string.Join(" | ", onSfx.Where(e => e.Text.Contains("超速解压缩")).Select(e => e.Text)));

    Console.WriteLine($"  注册目标 = {ShellMenuRegistrar.RegisteredTarget() ?? "(未知)"}");
    Console.WriteLine("  注：级联子项由资源管理器在展开时惰性创建，子命令本身由第 7 节从注册表读回验证。");
}

// ===================================================================== 9
Console.WriteLine();
Console.WriteLine("=== 9. 拖拽直出：零临时文件协议 ===");

// ---- 9.1 a two-file archive with a subdirectory ----
var dragDir = Path.Combine(work, "drag");
Directory.CreateDirectory(dragDir);
var dragSrc = Path.Combine(dragDir, "src");
Directory.CreateDirectory(Path.Combine(dragSrc, "sub"));

var payloadA = new byte[512 * 1024];
var payloadB = new byte[1024 * 1024];
new Random(11).NextBytes(payloadA);
new Random(22).NextBytes(payloadB);
File.WriteAllBytes(Path.Combine(dragSrc, "a.bin"), payloadA);
File.WriteAllBytes(Path.Combine(dragSrc, "sub", "b.bin"), payloadB);

var dragArchive = Path.Combine(dragDir, "bundle.7z");
// Archiving the *folder* (not the two files) is what puts a relative path into
// the archive, which is the case the drag-out naming has to preserve.
RunSevenZip($"a -t7z -mx1 \"{dragArchive}\" \"{dragSrc}\"");

IReadOnlyList<ArchiveEntry> dragEntries;
using (var engine = new ExtractionEngine(dragArchive))
{
    engine.Open();
    dragEntries = engine.Entries.ToArray();
}
foreach (var entry in dragEntries)
    Console.WriteLine($"      {entry.NormalizedPath,-16} {entry.Size,10:N0} B");

var dragFiles = dragEntries
    .Where(entry => !entry.IsDirectory)
    .Select(entry => new VirtualFileEntry(
        entry.NormalizedPath.Replace('/', '\\'),
        entry.NormalizedPath,
        entry.Size,
        entry.Modified))
    .ToList();

Check("9.1 归档内有两个文件（含子目录）", dragFiles.Count == 2, $"{dragFiles.Count} 个");

// Everything below must not create a single file; snapshot first.
var filesBeforeDrag = Directory.GetFiles(work, "*", SearchOption.AllDirectories)
    .OrderBy(p => p, StringComparer.Ordinal).ToArray();

// ---- 9.2 descriptor bytes ----
var descriptor = VirtualFileDataObject.BuildDescriptorBytes(dragFiles);
Check("9.2 FILEGROUPDESCRIPTORW 长度 = 4 + 584×N",
    descriptor.Length == 4 + 584 * dragFiles.Count, $"{descriptor.Length} B");
Check("9.2 cItems 正确", BitConverter.ToInt32(descriptor, 0) == dragFiles.Count);

var firstName = Encoding.Unicode.GetString(descriptor, 4 + 64, 40).TrimEnd('\0');
var firstSize = BitConverter.ToInt32(descriptor, 4 + 56) * 4294967296L +
                (uint)BitConverter.ToInt32(descriptor, 4 + 60);
var firstFlags = BitConverter.ToUInt32(descriptor, 4 + 0);
var secondName = Encoding.Unicode.GetString(descriptor, 4 + 584 + 64, 40).TrimEnd('\0');

Check("9.2 第一个条目名与大小正确",
    firstName == @"src\a.bin" && firstSize == payloadA.Length, $"{firstName} / {firstSize:N0}");
Check("9.2 dwFlags = ATTRIBUTES|FILESIZE|WRITESTIME",
    (firstFlags & 0x64) == 0x64, $"0x{firstFlags:X}");
Check("9.2 子目录条目保留相对路径", secondName == @"src\sub\b.bin", secondName);

// ---- 9.3 COM round trip: IDataObject through its real vtable ----
var dataObject = new VirtualFileDataObject(dragArchive, dragFiles);
var dataPtr = Marshal.GetComInterfaceForObject(dataObject, typeof(IOleDataObject));

var documentedIid = new Guid("0000010E-0000-0000-C000-000000000046");
var bogusIid = new Guid("0000010F-0000-0000-C000-000000000046");
var hit = Marshal.QueryInterface(dataPtr, ref documentedIid, out var qiPtr);
var miss = Marshal.QueryInterface(dataPtr, ref bogusIid, out _);
if (qiPtr != IntPtr.Zero) Marshal.Release(qiPtr);
Check("9.3 IDataObject 的 IID 正确（QI 命中、错 IID 落空）", hit == 0 && miss != 0,
    $"hit=0x{hit:X8} miss=0x{miss:X8}");

// The vtable is invoked slot by slot, exactly as the shell's drop target does.
var getData = VTableCaller.Slot<VTableCaller.GetDataDelegate>(dataPtr, 3);
var getDataHere = VTableCaller.Slot<VTableCaller.GetDataHereDelegate>(dataPtr, 4);
var queryGetData = VTableCaller.Slot<VTableCaller.QueryGetDataDelegate>(dataPtr, 5);
var enumFormatEtc = VTableCaller.Slot<VTableCaller.EnumFormatEtcDelegate>(dataPtr, 8);

var descriptorFormat = new FORMATETC
{
    cfFormat = VirtualFileDataObject.FileDescriptorFormat,
    dwAspect = 1,
    lindex = -1,
    tymed = 1,
};
Check("9.3 slot5 = QueryGetData(FileGroupDescriptorW) = S_OK",
    queryGetData(dataPtr, ref descriptorFormat) == 0);

// Slot 4 must be GetDataHere: ours answers E_NOTIMPL, whereas GetData would have
// succeeded. This is what pins the vtable order the shell will walk.
var hereMedium = default(STGMEDIUM);
var hereHr = getDataHere(dataPtr, ref descriptorFormat, ref hereMedium);
Check("9.3 slot4 是 GetDataHere（E_NOTIMPL 而不是 S_OK）",
    hereHr == unchecked((int)0x80004001), $"0x{hereHr:X8}");

var descriptorHr = getData(dataPtr, ref descriptorFormat, out var descriptorMedium);
Check("9.4 GetData 返回 HGLOBAL 介质",
    descriptorHr == 0 && descriptorMedium.tymed == 1 && descriptorMedium.unionmember != IntPtr.Zero,
    $"tymed={descriptorMedium.tymed}");

var globalBytes = (long)VTableCaller.GlobalSize(descriptorMedium.unionmember);
var fromGlobal = new byte[descriptor.Length];
if (descriptorMedium.unionmember != IntPtr.Zero)
{
    var locked = VTableCaller.GlobalLock(descriptorMedium.unionmember);
    Marshal.Copy(locked, fromGlobal, 0, fromGlobal.Length);
    VTableCaller.GlobalUnlock(descriptorMedium.unionmember);
}
Check("9.4 HGLOBAL 大小与内容与生成的一致",
    globalBytes == descriptor.Length && fromGlobal.SequenceEqual(descriptor),
    $"{globalBytes} B");
VTableCaller.ReleaseStgMedium(ref descriptorMedium);

// ---- 9.5 EnumFormatEtc (slot 8) ----
var enumHr = enumFormatEtc(dataPtr, 1, out var enumPtr);
var enumNext = VTableCaller.Slot<VTableCaller.NextDelegate>(enumPtr, 3);
var formatBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<FORMATETC>() * 4);
var fetchedPtr = Marshal.AllocHGlobal(4);
var nextHr = enumNext(enumPtr, 4, formatBuffer, fetchedPtr);
var fetchedCount = Marshal.ReadInt32(fetchedPtr);
var firstFormat = Marshal.PtrToStructure<FORMATETC>(formatBuffer);
Check("9.5 EnumFormatEtc 列出 3 种格式（S_FALSE 结束）",
    enumHr == 0 && nextHr == 1 && fetchedCount == 3, $"hr=0x{nextHr:X} count={fetchedCount}");
Check("9.5 首项为 FileGroupDescriptorW + TYMED_HGLOBAL",
    firstFormat.cfFormat == VirtualFileDataObject.FileDescriptorFormat && firstFormat.tymed == 1);
Marshal.FreeHGlobal(formatBuffer);
Marshal.FreeHGlobal(fetchedPtr);

// ---- 9.6 stream: Stat + full read through the COM vtable ----
static (long Total, byte[] Hash) DrainStream(
    VTableCaller.ReadDelegate read,
    IntPtr self,
    IntPtr buffer,
    IntPtr countPtr,
    int chunk)
{
    var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    long total = 0;
    while (true)
    {
        var hr = read(self, buffer, chunk, countPtr);
        if (hr != 0) throw new IOException($"IStream::Read 失败，HRESULT=0x{hr:X8}");
        var got = Marshal.ReadInt32(countPtr);
        if (got <= 0) break;
        var chunkBytes = new byte[got];
        Marshal.Copy(buffer, chunkBytes, 0, got);
        hasher.AppendData(chunkBytes);
        total += got;
    }
    return (total, hasher.GetHashAndReset());
}

var readBuffer = Marshal.AllocHGlobal(64 * 1024);
var readCountPtr = Marshal.AllocHGlobal(4);

var contentsFormat = new FORMATETC
{
    cfFormat = VirtualFileDataObject.FileContentsFormat,
    dwAspect = 1,
    lindex = 0,
    tymed = 4,
};
Check("9.6 slot5 = QueryGetData(FileContents, lindex=0) = S_OK",
    queryGetData(dataPtr, ref contentsFormat) == 0);

var streamHr = getData(dataPtr, ref contentsFormat, out var streamMedium);
Check("9.6 slot3 = GetData 返回 TYMED_ISTREAM",
    streamHr == 0 && streamMedium.tymed == 4 && streamMedium.unionmember != IntPtr.Zero,
    $"tymed={streamMedium.tymed}");

var dragStream = streamMedium.unionmember;
var streamRead = VTableCaller.Slot<VTableCaller.ReadDelegate>(dragStream, 3);
var streamWrite = VTableCaller.Slot<VTableCaller.WriteDelegate>(dragStream, 4);
var streamSeek = VTableCaller.Slot<VTableCaller.SeekDelegate>(dragStream, 5);
var streamStat = VTableCaller.Slot<VTableCaller.StatDelegate>(dragStream, 12);

var statPtr = Marshal.AllocHGlobal(Marshal.SizeOf<STATSTG>());
var writeHr = streamWrite(dragStream, IntPtr.Zero, 0, IntPtr.Zero);
var statHr = streamStat(dragStream, statPtr, 1);
var stat = Marshal.PtrToStructure<STATSTG>(statPtr);
Check("9.7 IStream 槽位顺序正确（4=Write 返回 E_NOTIMPL，12=Stat 可用）",
    writeHr == unchecked((int)0x80004001) && statHr == 0, $"write=0x{writeHr:X8} stat=0x{statHr:X8}");
Check("9.7 Stat 报告 STGTY_STREAM 与正确大小",
    stat.type == 2 && stat.cbSize == (ulong)payloadA.Length,
    $"type={stat.type} cbSize={stat.cbSize:N0}");
Marshal.FreeHGlobal(statPtr);

var (firstTotal, firstHash) = DrainStream(streamRead, dragStream, readBuffer, readCountPtr, 64 * 1024);
Check("9.8 流字节数与归档一致", firstTotal == payloadA.Length, $"{firstTotal:N0}/{payloadA.Length:N0}");
Check("9.8 流 SHA256 与原始一致",
    Convert.ToHexString(firstHash) == Convert.ToHexString(SHA256.HashData(payloadA)),
    Convert.ToHexString(firstHash)[..16] + "...");

// ---- 9.9 backward seek must still serve correct bytes ----
var seekHr = streamSeek(dragStream, 0, 0, IntPtr.Zero);
var replay = new byte[4096];
streamRead(dragStream, readBuffer, replay.Length, readCountPtr);
Marshal.Copy(readBuffer, replay, 0, replay.Length);
Check("9.9 回退 seek 后重读一致",
    seekHr == 0 && replay.AsSpan().SequenceEqual(payloadA.AsSpan(0, replay.Length)));

// ---- 9.10 second file ----
var secondFormat = new FORMATETC
{
    cfFormat = VirtualFileDataObject.FileContentsFormat,
    dwAspect = 1,
    lindex = 1,
    tymed = 4,
};
var secondHr = getData(dataPtr, ref secondFormat, out var secondMedium);
var secondStream = VTableCaller.Slot<VTableCaller.ReadDelegate>(secondMedium.unionmember, 3);
var (secondTotal, secondHash) = DrainStream(secondStream, secondMedium.unionmember, readBuffer, readCountPtr, 32 * 1024);
Check("9.10 第二个文件（子目录）同样正确",
    secondHr == 0 && secondTotal == payloadB.Length &&
    Convert.ToHexString(secondHash) == Convert.ToHexString(SHA256.HashData(payloadB)),
    $"{secondTotal:N0} B");

// ---- cleanup of the COM side ----
VTableCaller.ReleaseStgMedium(ref secondMedium);
VTableCaller.ReleaseStgMedium(ref streamMedium);
Marshal.Release(enumPtr);
Marshal.Release(dataPtr);
dataObject.Dispose();
Marshal.FreeHGlobal(readBuffer);
Marshal.FreeHGlobal(readCountPtr);

// ---- 9.11 multi-volume: dragging must never reclaim volumes ----
using (var multi = new VirtualFileDataObject(
           engineArc,
           new[] { new VirtualFileEntry("payload.bin", "payload.bin", 3_145_728, null) }))
{
    var multiFormat = new FORMATETC
    {
        cfFormat = VirtualFileDataObject.FileContentsFormat,
        dwAspect = 1,
        lindex = 0,
        tymed = 4,
    };
    var multiHr = multi.GetData(ref multiFormat, out var multiMedium);
    var multiStream = VTableCaller.Slot<VTableCaller.ReadDelegate>(multiMedium.unionmember, 3);

    var multiBuffer = Marshal.AllocHGlobal(64 * 1024);
    var multiCount = Marshal.AllocHGlobal(4);
    var (multiTotal, multiHash) = DrainStream(
        multiStream, multiMedium.unionmember, multiBuffer, multiCount, 64 * 1024);
    Marshal.FreeHGlobal(multiBuffer);
    Marshal.FreeHGlobal(multiCount);

    Check("9.11 分卷包拖出的内容 SHA256 一致",
        multiHr == 0 && multiTotal == 3_145_728 &&
        Convert.ToHexString(multiHash) == srcHash,
        $"{multiTotal:N0} B");

    VTableCaller.ReleaseStgMedium(ref multiMedium);
}

Check("9.11 拖拽后所有分卷仍在（拖出永不删卷）",
    engineVolumes.All(File.Exists), $"{engineVolumes.Count} 卷");

// ---- 9.12 the whole point: nothing was written to disk ----
var filesAfterDrag = Directory.GetFiles(work, "*", SearchOption.AllDirectories)
    .OrderBy(p => p, StringComparer.Ordinal).ToArray();
var created = filesAfterDrag.Except(filesBeforeDrag, StringComparer.OrdinalIgnoreCase).ToList();
Check("9.12 全程零临时文件（工作目录没有新增任何文件）",
    created.Count == 0,
    created.Count == 0 ? $"{filesAfterDrag.Length} 个文件，与拖拽前完全一致" : string.Join(" | ", created));

// ===================================================================== 10
Console.WriteLine();
Console.WriteLine("=== 10. ArchiveVerifier 批量校验 ===");

// A dedicated folder so the corrupt copy and the encrypted copy sit next to the
// good one; the tool must judge each on its own.
var verifyDir = Path.Combine(work, "verify");
Directory.CreateDirectory(verifyDir);

var vPayload = Path.Combine(verifyDir, "payload.bin");
File.WriteAllBytes(vPayload, payload);

var goodArc = Path.Combine(verifyDir, "good.7z");
RunSevenZip($"a -t7z -mx1 \"{goodArc}\" \"{vPayload}\"");

// Corrupt a run of bytes in the middle of the *data* region. The 7z header
// lives at the tail, so Open still succeeds and the failure surfaces as a CRC
// mismatch on payload.bin - exactly the case the tool exists to catch.
var badArc = Path.Combine(verifyDir, "bad.7z");
File.WriteAllBytes(badArc, File.ReadAllBytes(goodArc));
var badBytes = File.ReadAllBytes(badArc);
var flipAt = badBytes.Length / 2;
for (var i = flipAt; i < flipAt + 512 && i < badBytes.Length; i++) badBytes[i] ^= 0xFF;
File.WriteAllBytes(badArc, badBytes);

// ---- 10.1 healthy archive ----
var seenMembers = new List<string>();
var good = ArchiveVerifier.Verify(goodArc, onMember: (entry, ok) => seenMembers.Add($"{entry.NormalizedPath}:{ok}"));
Check("10.1 正常包判定通过", good.Ok && !good.Unverifiable, $"ok={good.Ok} reason={good.Reason}");
Check("10.1 无失败条目 / 明细为空", good.FailedCount == 0 && good.Issues.Count == 0,
    $"failed={good.FailedCount} issues={good.Issues.Count}");
Check("10.1 逐条目回调覆盖到 payload.bin 且通过",
    seenMembers.Contains("payload.bin:True"), string.Join(" | ", seenMembers));
Check("10.1 格式与条目数已读出", good.FormatName == "7z" && good.EntryCount == 1,
    $"{good.FormatName} / {good.EntryCount} 项");
Check("10.1 未写盘（TestOnly 语义）", !File.Exists(Path.Combine(verifyDir, "payload.bin.out")));

// ---- 10.2 corrupted archive ----
var bad = ArchiveVerifier.Verify(badArc);
Check("10.2 损坏包被判定失败", !bad.Ok && !bad.Unverifiable && !bad.Cancelled,
    $"ok={bad.Ok} unverifiable={bad.Unverifiable}");
Check("10.2 失败计数 > 0", bad.FailedCount > 0, $"{bad.FailedCount}");
Check("10.2 损坏清单指向 payload.bin",
    bad.Issues.Count > 0 && bad.Issues.Any(issue => issue.EntryPath == "payload.bin"),
    bad.Issues.Count > 0 ? bad.Issues[0].EntryPath + " / " + bad.Issues[0].Description : "(无明细)");
Check("10.2 给出了原因与建议", !string.IsNullOrWhiteSpace(bad.Reason) && !string.IsNullOrWhiteSpace(bad.Hint),
    $"{bad.Reason} -> {bad.Hint}");

// ---- 10.3 missing file ----
var missingReport = ArchiveVerifier.Verify(Path.Combine(verifyDir, "does-not-exist.7z"));
Check("10.3 缺失文件判定为无法校验并说明原因",
    missingReport.Unverifiable && missingReport.Reason == "找不到文件",
    $"unverifiable={missingReport.Unverifiable} reason={missingReport.Reason}");

// ---- 10.4 encrypted archive: no / wrong / right password ----
var encArc = Path.Combine(verifyDir, "locked.7z");
RunSevenZip($"a -t7z -mx1 -psecret123 -mhe=on \"{encArc}\" \"{vPayload}\"");

var noPass = ArchiveVerifier.Verify(encArc);
Check("10.4 加密包无口令时提示需要口令",
    noPass.NeedsPassword && !noPass.WrongPassword,
    $"needs={noPass.NeedsPassword} wrong={noPass.WrongPassword}");

var wrongPass = ArchiveVerifier.Verify(encArc, password: "nope");
Check("10.4 加密包口令错误时区分于需要口令",
    wrongPass.NeedsPassword && wrongPass.WrongPassword,
    $"needs={wrongPass.NeedsPassword} wrong={wrongPass.WrongPassword}");

var rightPass = ArchiveVerifier.Verify(encArc, password: "secret123");
Check("10.4 加密包口令正确时通过", rightPass.Ok && !rightPass.NeedsPassword,
    $"ok={rightPass.Ok} needs={rightPass.NeedsPassword} wrong={rightPass.WrongPassword} " +
    $"tested={rightPass.TestedCount} failed={rightPass.FailedCount} reason={rightPass.Reason}");

// ---- 10.5 an ordinary zip must never be mistaken for an encrypted one ----
var goodZip = Path.Combine(verifyDir, "good.zip");
RunSevenZip($"a -tzip -mx1 \"{goodZip}\" \"{vPayload}\"");
try
{
    using var diagEngine = new ExtractionEngine(goodZip);
    diagEngine.Open();
    Console.WriteLine($"      [diag] zip open ok: entries={diagEngine.Entries.Count} " +
                      $"encrypted={diagEngine.Entries.Count(e => e.IsEncrypted)} fmt={diagEngine.Format?.Name}");
}
catch (Exception ex)
{
    Console.WriteLine($"      [diag] zip open threw {ex.GetType().Name}: {ex.Message}");
}
var zipReport = ArchiveVerifier.Verify(goodZip);
Check("10.5 普通 zip 校验通过（口令误判回归）",
    zipReport.Ok && !zipReport.NeedsPassword,
    $"ok={zipReport.Ok} needs={zipReport.NeedsPassword} wrong={zipReport.WrongPassword} " +
    $"entries={zipReport.EntryCount} tested={zipReport.TestedCount} failed={zipReport.FailedCount} reason={zipReport.Reason}");

// ---- 10.6 scanner integration: a folder of archives folds volumes to one entry ----
var scannedVerify = ArchiveScanner.Scan(verifyDir);
Check("10.6 目录扫描列出每个归档一次",
    scannedVerify.Count == 4 &&
    new[] { "bad.7z", "good.7z", "good.zip", "locked.7z" }.All(n => scannedVerify.Any(c => c.DisplayName == n)),
    string.Join(", ", scannedVerify.Select(c => c.DisplayName)));

// ===================================================================== 11
Console.WriteLine();
Console.WriteLine("=== 11. RAR 分卷（独立卷布局 + 自解压首卷）===");

// RAR volumes are separate archives rather than byte slices of one stream, so
// 7-Zip stitches them together through IArchiveOpenVolumeCallback: it asks for
// kpidName, derives the next volume's name (CVolumeName::InitName maps .exe to
// .rar), then asks for that stream. Concatenating them - correct for .7z.001 -
// decodes as corrupt data. Only WinRAR's CLI can build these, so the extraction
// half of this section skips itself when Rar.exe is absent.

// ---- 11.0 naming rules, which need no RAR tooling ----
var legacyDir = Path.Combine(work, "legacy-rar");
Directory.CreateDirectory(legacyDir);
foreach (var n in new[] { "old.rar", "old.r00", "old.r01" })
    File.WriteAllBytes(Path.Combine(legacyDir, n), new byte[] { 0 });

var legacy = VolumeDetector.DetectGroup(Path.Combine(legacyDir, "old.rar"));
Check("11.0 老式 RAR 命名（.rar + .r00 + .r01）识别为独立卷组",
    legacy is { Style: VolumeStyle.RarOldStyle, IsIndependentVolumes: true } && legacy.Volumes.Count == 3,
    legacy is null ? "(未识别)" : $"{legacy.Style} · {legacy.Volumes.Count} 卷");

var partExeDir = Path.Combine(work, "sfx-naming");
Directory.CreateDirectory(partExeDir);
foreach (var n in new[] { "Build.part01.exe", "Build.part02.rar", "Build.part03.rar" })
    File.WriteAllBytes(Path.Combine(partExeDir, n), new byte[] { 0 });

var partExe = VolumeDetector.DetectGroup(Path.Combine(partExeDir, "Build.part01.exe"));
Check("11.0 .part01.exe 首卷 + .part02.rar 识别为同一组",
    partExe is { Style: VolumeStyle.RarNewStyle, IsIndependentVolumes: true } && partExe.Volumes.Count == 3 &&
    partExe.Volumes[0].FileName.Equals("Build.part01.exe", StringComparison.OrdinalIgnoreCase),
    partExe is null ? "(未识别)" : $"{partExe.Volumes.Count} 卷 · 首卷 {partExe.Volumes[0].FileName}");

var rarExe = @"C:\Program Files\WinRAR\Rar.exe";
if (!File.Exists(rarExe))
{
    Console.WriteLine("  跳过：本机没有 WinRAR 命令行（Rar.exe），造不出 RAR 分卷");
}
else
{
    var rarDir = Path.Combine(work, "rar");
    var rarSrc = Path.Combine(rarDir, "src");
    Directory.CreateDirectory(rarSrc);

    var rarPayload = new byte[3 * 1024 * 1024];
    new Random(777).NextBytes(rarPayload);
    File.WriteAllBytes(Path.Combine(rarSrc, "payload.bin"), rarPayload);
    File.WriteAllText(Path.Combine(rarSrc, "readme.txt"), "rar volume probe");
    var rarHash = Convert.ToHexString(SHA256.HashData(rarPayload));

    static string HashOf(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- 11.1 plain .partN.rar ----
    RunRar(rarExe, rarSrc, Path.Combine(rarDir, "vol.rar"), "-v1m", "-m0");
    var rarVolumes = Directory.GetFiles(rarDir, "vol.part*.rar")
        .OrderBy(p => p, StringComparer.Ordinal).ToList();
    Check("11.1 造出多卷 RAR", rarVolumes.Count > 1, $"{rarVolumes.Count} 卷");

    using (var engine = new ExtractionEngine(rarVolumes[0]))
    {
        engine.Open();
        Check("11.1 识别为 RAR 分卷组（独立卷布局）",
            engine.IsMultiVolume && engine.Group!.Style == VolumeStyle.RarNewStyle &&
            engine.Group.IsIndependentVolumes,
            $"{engine.VolumeCount} 卷 / {engine.Format?.Name}");
    }

    var rarOut = Path.Combine(rarDir, "out");
    ExtractionResult rarResult;
    using (var engine = new ExtractionEngine(rarVolumes[0]))
    {
        rarResult = engine.Extract(new ExtractionOptions { OutputDirectory = rarOut });
    }

    Check("11.2 分卷 RAR 解压成功", rarResult.Ok && rarResult.Extracted == 2, $"{rarResult.Extracted} 项");
    Check("11.2 内容与原始逐字节一致",
        File.Exists(Path.Combine(rarOut, "payload.bin")) &&
        HashOf(Path.Combine(rarOut, "payload.bin")) == rarHash);

    // ---- 11.3 self-extracting first volume: name.part1.exe ----
    RunRar(rarExe, rarSrc, Path.Combine(rarDir, "Build.25778117.exe"), "-v1m", "-m0", "-sfx");
    var sfxFirst = Directory.GetFiles(rarDir, "Build.*.part1.exe").FirstOrDefault();
    if (sfxFirst is null)
    {
        Console.WriteLine("  跳过 SFX：Rar.exe 没有产出 .part1.exe");
    }
    else
    {
        using var sfxEngine = new ExtractionEngine(sfxFirst);
        sfxEngine.Open();
        Check("11.3 自解压首卷 .part1.exe 被当作分卷组",
            sfxEngine.IsMultiVolume && sfxEngine.Group!.IsIndependentVolumes,
            $"{sfxEngine.VolumeCount} 卷 / 首卷 {sfxEngine.Group!.Volumes[0].FileName}");

        var sfxOut = Path.Combine(rarDir, "out-sfx");
        var sfxResult = sfxEngine.Extract(new ExtractionOptions { OutputDirectory = sfxOut });
        Check("11.3 .part1.exe 解压内容正确",
            sfxResult.Ok && File.Exists(Path.Combine(sfxOut, "payload.bin")) &&
            HashOf(Path.Combine(sfxOut, "payload.bin")) == rarHash,
            $"{sfxResult.Extracted} 项");
    }

    // ---- 11.4 delete each volume as it is consumed ----
    var sweepDir = Path.Combine(rarDir, "sweep");
    Directory.CreateDirectory(sweepDir);
    foreach (var v in rarVolumes) File.Copy(v, Path.Combine(sweepDir, Path.GetFileName(v)));
    var sweepVolumes = Directory.GetFiles(sweepDir, "vol.part*.rar")
        .OrderBy(p => p, StringComparer.Ordinal).ToList();
    var sweepOut = Path.Combine(rarDir, "out-sweep");

    ExtractionResult sweepResult;
    var sweepTimeline = new List<(int Done, int Total, int Deleted)>();
    using (var engine = new ExtractionEngine(sweepVolumes[0]))
    {
        sweepResult = engine.Extract(new ExtractionOptions
        {
            OutputDirectory = sweepOut,
            DeleteSpentVolumes = true,
            KeepLastVolumes = 1,
        }, progress =>
        {
            // A RAR volume deleted before the decoder is finished can be read
            // again: each member's header, and the set trailer, live wherever they
            // were written. That read fails, 7z.dll reports the archive as
            // truncated, and the run ends in "已损坏、不完整" even though every
            // member was written correctly. This timeline is that regression guard.
            lock (sweepTimeline) sweepTimeline.Add((progress.EntriesDone, progress.EntriesTotal, progress.VolumesDeleted));
        });

        var states = engine.VolumeStates();
        Console.WriteLine("      每卷回收状态: " + string.Join(" | ",
            states.Select(s => $"[{s.Index}] {s.FileName} served={s.Served} spent={s.Spent} safe={s.DeletionSafe} " +
                               $"covered={s.CoveredEnd:N0}/{s.Length:N0}")));
    }

    var earlyDeletes = sweepTimeline.Where(t => t.Deleted > 0 && t.Done < t.Total).ToList();
    Check("11.5 独立卷（RAR）绝不在解压中途被删 —— 只在全部条目结束后回收",
        earlyDeletes.Count == 0,
        earlyDeletes.Count == 0
            ? $"{sweepTimeline.Count} 个进度采样点，中途删除 0 次"
            : string.Join(" | ", earlyDeletes.Take(5).Select(t => $"{t.Done}/{t.Total} 时已删 {t.Deleted} 卷")));

    // 11.6 The opposite must stay true for sliced sets: they are one strictly
    // forward-read merged stream, so a volume the decoder has passed can never be
    // read again - which is the whole point of reclaiming space DURING extraction.
    // Without this, "only delete at the end" would silently become the behaviour
    // everywhere and a set larger than the free space would stop being extractable.
    var slicedDir = Path.Combine(rarDir, "sliced");
    Directory.CreateDirectory(slicedDir);
    var slicedSrc = Path.Combine(slicedDir, "payload.bin");
    File.WriteAllBytes(slicedSrc, rarPayload);
    RunSevenZip($"a -t7z -mx1 -v1m \"{Path.Combine(slicedDir, "slice.7z")}\" \"{slicedSrc}\"");

    var slicedVolumes = Directory.GetFiles(slicedDir, "slice.7z.*")
        .OrderBy(p => p, StringComparer.Ordinal).ToList();
    var slicedTimeline = new List<(int Done, int Total, int Deleted)>();
    using (var engine = new ExtractionEngine(slicedVolumes[0]))
    {
        engine.Extract(new ExtractionOptions
        {
            OutputDirectory = Path.Combine(rarDir, "out-sliced"),
            DeleteSpentVolumes = true,
            KeepLastVolumes = 1,
        }, progress => { lock (slicedTimeline) slicedTimeline.Add((progress.EntriesDone, progress.EntriesTotal, progress.VolumesDeleted)); });
    }

    var slicedEarly = slicedTimeline.Where(t => t.Deleted > 0).ToList();
    Check("11.6 切片卷（.7z.001）仍然边解边删（功能没被一刀砍掉）",
        slicedVolumes.Count > 1 && slicedEarly.Count > 0,
        $"{slicedVolumes.Count} 卷 · {slicedTimeline.Count} 个采样点，其中 {slicedEarly.Count} 个已出现删除");

    Check("11.4 分卷 RAR 边解边删：回收 N-1 卷",
        sweepResult.DeletedVolumes.Count == sweepVolumes.Count - 1,
        $"{sweepResult.DeletedVolumes.Count}/{sweepVolumes.Count} 卷 · 回收 {sweepResult.BytesFreed:N0} B");
    Check("11.4 最后一卷仍在（保险丝生效）", File.Exists(sweepVolumes[^1]));
    Check("11.4 边删边解产物仍正确",
        File.Exists(Path.Combine(sweepOut, "payload.bin")) &&
        HashOf(Path.Combine(sweepOut, "payload.bin")) == rarHash);
}

// ===================================================================== 12
Console.WriteLine();
Console.WriteLine("=== 12. 右键菜单的可见性判定（共享源码，两个菜单都用它）===");

// The classic menu gates verbs with an AQS extension list and the Windows 11
// modern menu gates them with this code. They disagreed once: name.part01.exe
// - the first volume of a self-extracting RAR set - matched neither, so every
// extract verb including "delete each volume as it is spent" was hidden exactly
// where it mattered. The code half is asserted here; the AQS half is asserted by
// section 8.4 against a real shell menu.
Check("12.1 普通压缩包判定", ArchiveNameHeuristics.LooksLikeArchive("a.7z")
    && ArchiveNameHeuristics.LooksLikeArchive("a.zip")
    && ArchiveNameHeuristics.LooksLikeArchive("a.part1.rar")
    && ArchiveNameHeuristics.LooksLikeArchive("a.r00"));

Check("12.2 自解压首卷 .part01.exe 必须算压缩包（本轮修的 bug）",
    ArchiveNameHeuristics.LooksLikeArchive(@"C:\x\Build.25778117.part01.exe") &&
    ArchiveNameHeuristics.LooksLikeArchive("Build.part1.exe") &&
    ArchiveNameHeuristics.LooksLikeArchive("Build.part99.EXE"),
    "Build.25778117.part01.exe / Build.part1.exe / Build.part99.EXE");

Check("12.3 普通 exe 不算压缩包（否则解压动词会挂到全盘每个程序上）",
    !ArchiveNameHeuristics.LooksLikeArchive(@"C:\Windows\notepad.exe") &&
    !ArchiveNameHeuristics.LooksLikeArchive("setup.exe") &&
    !ArchiveNameHeuristics.LooksLikeArchive("my.parts.exe") &&
    !ArchiveNameHeuristics.LooksLikeArchive("part01.exe"));

Check("12.4 数字切片与 PKZIP 分卷", ArchiveNameHeuristics.LooksLikeArchive("a.7z.001")
    && ArchiveNameHeuristics.LooksLikeArchive("a.z01")
    && ArchiveNameHeuristics.LooksLikeArchive("a.s05"));

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "===== 全部通过 ====="
    : $"===== {failures} 项失败 =====");
Console.WriteLine($"工作目录: {work}");
return failures;

// ---------------------------------------------------------------------------

void RunRar(string exe, string workingDir, string archive, params string[] extraArgs)
{
    var psi = new ProcessStartInfo(exe)
    {
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    psi.ArgumentList.Add("a");
    foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);
    psi.ArgumentList.Add(archive);
    psi.ArgumentList.Add("payload.bin");
    psi.ArgumentList.Add("readme.txt");

    using var process = Process.Start(psi)!;
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Console.WriteLine(process.StandardOutput.ReadToEnd());
        Console.WriteLine(process.StandardError.ReadToEnd());
        throw new InvalidOperationException($"Rar.exe failed ({process.ExitCode})");
    }
}

// ---------------------------------------------------------------------------

static bool IsMonotonic(IReadOnlyList<ExtractionProgress> log)
{
    for (var i = 1; i < log.Count; i++)
    {
        if (log[i].BytesDone < log[i - 1].BytesDone) return false;
        if (log[i].EntriesDone < log[i - 1].EntriesDone) return false;
    }
    return true;
}

// ---------------------------------------------------------------------------

void RunSevenZip(string args)
{
    var psi = new ProcessStartInfo(SevenZipExe, args)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.WriteLine(p.StandardOutput.ReadToEnd());
        Console.WriteLine(p.StandardError.ReadToEnd());
        throw new InvalidOperationException($"7z.exe failed ({p.ExitCode})");
    }
}

static string? GetString(IInArchive arc, uint index, uint propId)
{
    var pv = PropVariant.Create();
    try
    {
        if (arc.GetProperty(index, propId, ref pv) != 0) return null;
        return pv.AsString();
    }
    finally { pv.Clear(); }
}

static ulong GetUInt64(IInArchive arc, uint index, uint propId)
{
    var pv = PropVariant.Create();
    try
    {
        if (arc.GetProperty(index, propId, ref pv) != 0) return 0;
        return pv.AsUInt64();
    }
    finally { pv.Clear(); }
}

static ulong GetArchiveUInt64(IInArchive arc, uint propId)
{
    var pv = PropVariant.Create();
    try
    {
        if (arc.GetArchiveProperty(propId, ref pv) != 0) return 0;
        return pv.AsUInt64();
    }
    finally { pv.Clear(); }
}

// ===========================================================================
// Extraction callbacks. These are the COM objects 7z.dll calls into; their
// vtable order is pinned by the interfaces in SevenZipInterfaces.cs.
// ===========================================================================

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class OutputSink : ISequentialOutStream
{
    private readonly FileStream _file;
    private readonly Action<long> _onBytes;

    public OutputSink(FileStream file, Action<long> onBytes)
    {
        _file = file;
        _onBytes = onBytes;
    }

    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
        if (size == 0 || data == IntPtr.Zero) return 0;

        var buffer = new byte[size];
        Marshal.Copy(data, buffer, 0, (int)size);
        _file.Write(buffer, 0, buffer.Length);
        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, (int)size);
        _onBytes(size);
        return 0;
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExtractSink : IArchiveExtractCallback
{
    private readonly IInArchive _archive;
    private readonly string _outDir;
    private readonly VolumeAwareStream _stream;

    private FileStream? _current;
    private string? _currentPath;
    private long _totalWritten;
    private readonly List<object> _liveStreams = new();

    public ExtractSink(IInArchive archive, string outDir, VolumeAwareStream stream)
    {
        _archive = archive;
        _outDir = outDir;
        _stream = stream;
    }

    public long TotalWritten => _totalWritten;

    public int SetTotal(ulong total)
    {
        Console.WriteLine($"      SetTotal: {total:N0} B");
        return 0;
    }

    public int SetCompleted(IntPtr completeValue)
    {
        // 7-Zip may pass NULL before SetTotal; guard before dereferencing.
        if (completeValue != IntPtr.Zero)
        {
            var done = (ulong)Marshal.ReadInt64(completeValue);
            var pct = _stream.TotalBytes > 0
                ? 100.0 * _stream.HighWaterMark / _stream.TotalBytes
                : 0;
            Console.WriteLine(
                $"      progress: {done,12:N0} B   archive-stream {pct,5:F1}%   " +
                $"vol {_stream.Group.VolumeIndexAt(_stream.Position) + 1}/{_stream.Group.Volumes.Count}");
        }
        return 0;
    }

    public int GetStream(uint index, IntPtr outStream, int askExtractMode)
    {
        if (outStream != IntPtr.Zero) Marshal.WriteIntPtr(outStream, IntPtr.Zero);
        if (askExtractMode != SevenZipNative.AskMode.Extract) return 0;

        var name = GetString(_archive, index, SevenZipNative.PropId.Path);
        if (string.IsNullOrEmpty(name)) return 0;

        var isDir = GetUInt64(_archive, index, SevenZipNative.PropId.IsDir) != 0;
        if (isDir) return 0;

        var safe = name.Replace('\\', '/').TrimStart('/');
        var target = Path.GetFullPath(Path.Combine(_outDir, safe));
        if (!target.StartsWith(Path.GetFullPath(_outDir), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"      !! refusing traversal: {name}");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        _current = File.Create(target);
        _currentPath = target;
        Console.WriteLine($"      -> {name}");
        // Hand 7-Zip an explicit ISequentialOutStream pointer. The managed object
        // must stay rooted: the CCW's only other reference lives on the native side.
        var sink = new OutputSink(_current, n => _totalWritten += n);
        _liveStreams.Add(sink);
        var ptr = Marshal.GetComInterfaceForObject(sink, typeof(ISequentialOutStream));
        if (outStream != IntPtr.Zero) Marshal.WriteIntPtr(outStream, ptr);
        return 0;
    }

    public int PrepareOperation(int askExtractMode) => 0;

    public int SetOperationResult(int opRes)
    {
        _current?.Dispose();
        _current = null;

        if (opRes != SevenZipNative.OperationResult.Ok)
        {
            Console.WriteLine($"      !! opRes={SevenZipNative.OperationResult.Describe(opRes)}" +
                              $"  deleting {Path.GetFileName(_currentPath ?? "")}");
            if (_currentPath is not null) File.Delete(_currentPath);
        }

        _currentPath = null;
        return 0;
    }

    // Local copies: top-level local functions are not visible from a type body.
    private static string? GetString(IInArchive arc, uint index, uint propId)
    {
        var pv = PropVariant.Create();
        try
        {
            if (arc.GetProperty(index, propId, ref pv) != 0) return null;
            return pv.AsString();
        }
        finally { pv.Clear(); }
    }

    private static ulong GetUInt64(IInArchive arc, uint index, uint propId)
    {
        var pv = PropVariant.Create();
        try
        {
            if (arc.GetProperty(index, propId, ref pv) != 0) return 0;
            return pv.AsUInt64();
        }
        finally { pv.Clear(); }
    }
}
