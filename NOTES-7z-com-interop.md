# SliceArc — 7z.dll COM 互操作踩坑记录

> 本文记录的是**实测结论**，不是推测。所有结论都在 `tests/SliceArc.Probe` 里跑通过。
> 日期：2026-10-12 · 环境：.NET 8.0.425 / 7z.dll 26.04 / x64

---

## 一、7-Zip 接口 GUID 的生成规则

7-Zip 的接口 GUID 不是随机的，全部由 `CPP/7zip/IDecl.h` 的宏生成：

```c
#define Z7_DECL_IFACE_7ZIP_SUB(i, _base, groupId, subId) \
  Z7_DEFINE_GUID(IID_ ## i, \
    k_7zip_GUID_Data1,          /* 0x23170F69 */ \
    k_7zip_GUID_Data2,          /* 0x40C1     */ \
    k_7zip_GUID_Data3_Common,   /* 0x278A     */ \
    0, 0, 0, (groupId), 0, (subId), 0, 0);
```

即文本形式恒为：

```
23170F69-40C1-278A-0000-00GG00SS0000
                          ^^  ^^
                          |   +-- subId
                          +------ groupId
```

已核对的值：

| 接口 | group | sub | GUID |
|---|---|---|---|
| `ISequentialInStream` | 3 | 0x01 | `23170F69-40C1-278A-0000-000300010000` |
| `ISequentialOutStream` | 3 | 0x02 | `23170F69-40C1-278A-0000-000300020000` |
| `IInStream` | 3 | 0x03 | `23170F69-40C1-278A-0000-000300030000` |
| `IOutStream` | 3 | 0x04 | `23170F69-40C1-278A-0000-000300040000` |
| `IStreamGetSize` | 3 | 0x06 | `23170F69-40C1-278A-0000-000300060000` |
| `IProgress` | 0 | 5 | `23170F69-40C1-278A-0000-000000050000` |
| `IArchiveOpenCallback` | 6 | 0x10 | `23170F69-40C1-278A-0000-000600100000` |
| `IArchiveExtractCallback` | 6 | 0x20 | `23170F69-40C1-278A-0000-000600200000` |
| `IArchiveOpenVolumeCallback` | 6 | 0x30 | `23170F69-40C1-278A-0000-000600300000` |
| `IInArchiveGetStream` | 6 | 0x40 | `23170F69-40C1-278A-0000-000600400000` |
| `IInArchive` | 6 | 0x60 | `23170F69-40C1-278A-0000-000600600000` |

**不要硬编码 CLSID。** 用 `GetHandlerProperty2(i, kClassID)` 遍历枚举——它返回
`VT_BSTR`，内容是 **16 字节的原始 GUID 二进制**（不是格式化字符串），
`new Guid(bytes)` 直接可用。这样 7-Zip 升级换 CLSID 也不会崩。

---

## 二、三个必须遵守的坑（每个都会让 CLR 崩或返回 E_POINTER）

### 坑 1：vtable 必须扁平声明，绝不能靠接口继承

C++ 侧的接口是继承的：

```cpp
struct IInStream: public ISequentialInStream { Seek(...); };
struct IArchiveExtractCallback: public IProgress { GetStream(...); ... };
```

**但在 C# 里写成 `interface IInStream : ISequentialInStream` 会让 CCW 生成错误的槽位。**

症状：`7z.dll` 一调过来就是 `Fatal error. Internal CLR error. (0x80131506)`。

正确做法——把**全部槽位平铺**在一个接口里：

```csharp
// 真实 vtable: [QueryInterface, AddRef, Release, Read, Seek]
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300030000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
    [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
}
```

`IArchiveExtractCallback` 同理，8 个槽位全平铺：

```csharp
// 真实 vtable: [QI, AddRef, Release, SetTotal, SetCompleted,
//               GetStream, PrepareOperation, SetOperationResult]
public interface IArchiveExtractCallback
{
    [PreserveSig] int SetTotal(ulong total);          // IProgress
    [PreserveSig] int SetCompleted(IntPtr completeValue); // IProgress
    [PreserveSig] int GetStream(uint index, IntPtr outStream, int askExtractMode);
    [PreserveSig] int PrepareOperation(int askExtractMode);
    [PreserveSig] int SetOperationResult(int opRes);
}
```

### 坑 2：所有输出参数必须用 `IntPtr`，不能用 `out`

```csharp
// ✗ 会让 Open 返回 E_POINTER (0x80004003)
int Read(IntPtr data, uint size, out uint processedSize);

// ✓ 手动写回
int Read(IntPtr data, uint size, IntPtr processedSize);
// 实现里：if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, n);
```

原因：7-Zip 对 `processedSize` / `newPosition` 明确允许传 `NULL`
（`IStream.h` 注释：*"The callee can allow (processedSize == NULL) for compatibility reasons"*），
而 CCW 对 `out` 参数的 marshal 路径会踩到 `E_POINTER`。

`IInArchive.Open` / `Extract` 的接口参数也要传**裸指针**，不能用 `[MarshalAs(UnmanagedType.Interface)]`：

```csharp
[PreserveSig] int Open(IntPtr stream, IntPtr maxCheckStartPosition, IntPtr openCallback);
[PreserveSig] int Extract(uint[]? indices, uint numItems, int testMode, IntPtr extractCallback);
```

调用侧：

```csharp
var p = Marshal.GetComInterfaceForObject(stream, typeof(IInStream));
try { hr = archive.Open(p, maxCheck, IntPtr.Zero); }
finally { Marshal.Release(p); }
```

**为什么不能用托管接口参数**：默认 marshaler 交给原生侧的是 **IUnknown vtable**，
期望被调用方自己 `QueryInterface`。但 7-Zip 拿到指针就直接调 `Read`/`Seek`，
不做 QI —— 于是调到了错误的槽位。

### 坑 3：`IArchiveExtractCallback::GetStream` 的返回流要自己管引用

```csharp
var sink = new OutputSink(...);
_liveStreams.Add(sink);                     // 必须自己保住托管对象
var ptr = Marshal.GetComInterfaceForObject(sink, typeof(ISequentialOutStream));
Marshal.WriteIntPtr(outStream, ptr);        // 引用交给 7z.dll，不要 Release
```

---

## 三、分卷消费判定：为什么「最大偏移」是错的

这是整个产品最核心的算法，我一开始写错了两次。

### 错误模型 A：高水位标记

```
spent(V) := maxReadOffset >= V.End
```

**错。** 打开 7z 归档时，7-Zip 会先 seek 到**尾部**读 header，
于是 `maxReadOffset` 瞬间跳到 EOF，而 V0..Vn-2 **一个字节都没读过**。
实测：4 卷全部被误判为 `spent=True`，一旦真删就把归档毁了。

### 错误模型 B：单卷字节覆盖

```
spent(V) := volumeReadEnd[V] >= V.Length
```

**也错。** `volumeReadEnd` 记录的是**局部最大结束偏移**，不是「全覆盖」。
最后一个分卷的数据区 `[0,231)` 和 header 区 `[231,329)` 是**两趟**读的，
最大偏移到了 329 就以为读全了，但 `[0,231)` 可能还没碰。

### 正确模型：从 0 开始的区间覆盖集

维护一个**已读区间的合并集合**，判据是 `[0, V.End)` 被**单个连续区间**覆盖。

```csharp
private readonly List<(long Start, long End)> _covered = new();

public bool IsVolumeSpent(int index)
{
    var end = _group.VolumeEnd(index);
    foreach (var (start, coveredEnd) in _covered)
        if (start <= 0 && coveredEnd >= end) return true;
    return false;
}

private void MarkCovered(long start, long end)
{
    if (end <= start) return;
    var i = 0;
    while (i < _covered.Count && _covered[i].End < start) i++;
    var newStart = start; var newEnd = end;
    while (i < _covered.Count && _covered[i].Start <= newEnd)
    {
        if (_covered[i].Start < newStart) newStart = _covered[i].Start;
        if (_covered[i].End   > newEnd)   newEnd   = _covered[i].End;
        _covered.RemoveAt(i);
    }
    _covered.Insert(i, (newStart, newEnd));
}
```

**关键点：`BeginExtractionTracking()` 绝不能清空 `_covered`。**

因为 Open 阶段读的 `[0,32)`（签名）和 `[3145959,3146057)`（尾部 header）
都是**合法消费**，必须计入。Extract 阶段补上中间 `[32,3145959)`，
三个区间合并成 `[0,3146057)`，此时**所有分卷才真正可删**。

### 附带结论：回退 seek 是正常的

`Extract` 一开始 7-Zip 会 `Seek(32, STREAM_SEEK_SET)` 回到数据区起点。
实测 `backSeeks=1` 是**预期行为**，不能拿它当「读取不可预测」的判据。

---

## 四、实测数据（2026-10-12）

测试包：3 MiB 不可压缩随机数据 → `7z a -mx1 -v1m` → 4 卷。

```
[CCW] Seek #1 off=0 origin=1 pos=0                  # 记录起始位置
[CCW] GetSize #1                                     # QI 到 IStreamGetSize
[CCW] Read  #1 req=32  got=32  head=377ABCAF271C0004 # 读 7z 签名头
[CCW] Seek #2 off=3145927 origin=1 pos=32            # 跳到尾部 header
[CCW] Read  #2 req=98  got=98                        # 读 header
--- Open 完成，HRESULT=0 ---
[CCW] Seek #3 off=32 origin=0 pos=3146057            # Extract 回到数据区（回退 seek）
[CCW] Read  #3 req=262144 got=262144                 # 256 KiB 分块顺序读
...
--- Extract 完成，SHA256 与原始一致 ---
```

性能相关：7-Zip 的读取块大小是 **256 KiB**，不是 64 KiB。
缓冲区按此规划。

---

## 五、其他

- `7z.dll` 导出：`CreateObject` / `GetNumberOfFormats` / `GetHandlerProperty2` /
  `SetLargePageMode`，全部 `__stdcall`（x64 上唯一约定）。
- `PROPVARIANT` 在 x64 上是 **24 字节**，`[StructLayout(LayoutKind.Explicit, Size = 24)]`。
- 打开分卷时 `maxCheckStartPosition` **要传非 NULL**（指向 `UInt64`，值 `1<<23`）。
  传 NULL 虽然头文件说合法，但某些 handler 表现不稳定。
- `IInArchive` 的方法顺序必须严格照抄 `IArchive.h` 的 `Z7_IFACEM_IInArchive`：
  `Open, Close, GetNumberOfItems, GetProperty, Extract, GetArchiveProperty,
   GetNumberOfProperties, GetPropertyInfo, GetNumberOfArchiveProperties, GetArchivePropertyInfo`。
- 项目必须开 `<BuiltInComInteropSupport>true</BuiltInComInteropSupport>`，
  否则 CCW 建不起来。
- `File.OpenHandle(..., FileShare.ReadWrite | FileShare.Delete, ...)` ——
  `FileShare.Delete` 是关键，否则删分卷时文件被占用。
  但**要真正回收磁盘空间必须先把句柄关掉**（`ReleaseVolumeHandle`）。

---

## 六、分卷的两种布局：切片卷 vs 独立卷（2026-10-13 实锤）

**这一节是分卷支持的命门。** 分卷不是一个概念，是两种完全不同的东西，喂错了解码器就是"数据损坏"。

| 布局 | 例子 | 每一卷是什么 | 怎么喂 7z.dll |
|---|---|---|---|
| **切片卷** | `a.7z.001`、`b.zip.001`、`c.z01…c.zip` | 一条归档流按字节切开 | **拼成一条连续流**（现在的做法，实测正确） |
| **独立卷** | `v.part1.rar`、`v.r00`、**`Build.part01.exe`** | **各自都是完整归档**，靠头里的卷号串起来 | **只给第一卷**，剩下的靠 `IArchiveOpenVolumeCallback` 按名字取 |

RAR 属于独立卷。把 RAR 各卷拼成一条流 → 解压报"数据损坏"
（2026-10-13 实测：4 卷 RAR 拼接后 `解压 0 项 · 失败 1 项 · 原因：数据损坏`，而 7z.exe 正常）。
判据在 `VolumeGroup.IsIndependentVolumes`。

### 契约（读 `.refs` 源码逐条确认）

`Archive/Rar/RarHandler.cpp` L1065-1125：

```cpp
openCallback->QueryInterface(IID_IArchiveOpenVolumeCallback, &openVolumeCallback);
...
if (!openVolumeCallback) break;                       // 拿不到就只认第一卷
RINOK(openVolumeCallback->GetProperty(kpidName, &prop))   // ① 问归档名
baseName = prop.bstrVal;
seqName.InitName(baseName, _arcInfo.HaveNewVolumeName()); // ② 由它推出下一卷名
volName = seqName.GetNextName();
HRESULT result = openVolumeCallback->GetStream(volName, &inStream);  // ③ 要下一卷的流
```

三点必须照做：

1. **`GetProperty(kpidName)` 要返回"文件名 + 扩展名"**（不是全路径、不是去掉扩展名的基名）。
   `RarVol.h` 的 `CVolumeName::InitName` 靠这个字符串推下一卷：
   - 结尾 `.rar` → `_after=".rar"`，再去掉尾部数字并 +1 → `v.part2.rar`
   - **结尾 `.exe` → `_after=".rar"`** → `Build.25778117.part01.exe` 的下一卷就是
     `Build.25778117.part02.rar` —— **7-Zip 原生就支持 `.part01.exe` 首卷，不用我们特殊处理**，
     只要把名字如实报上去
   - 其余（老式）→ `v.rar` + `v.r00` + `v.r01`…
2. **`GetStream(name, IInStream**)` 的返回流必须是裸指针**（本项目坑 2）。
   原来的声明写的是 `out IInStream?`，那会被 marshaler 变成 IUnknown vtable → 调到错槽位。
   改成 `IntPtr outStream` + `Marshal.GetComInterfaceForObject(...)`。
3. **找不到就 `S_FALSE` + NULL**，handler 据此判断"卷到此为止"。

### 实现要点（`ExtractionEngine`）

- **`_volumeCallbackArmed`**：只对"非切片多卷"开启。切片多卷已经在一整条流里了，
  再让 Split/Zip handler 去找兄弟卷等于改一条已经验证过的路径，没有收益。
- 独立卷模式下 **每个卷一个 `VolumeAwareStream`**，所以 `IsVolumeSpent(i)` 问的是"这一卷自己读完了没"，
  而不是合并流的覆盖区间。
- **删除判据要留结构尾部余量**：RAR 卷末尾有个 end-of-archive 记录，解码器**根本不读**
  （实测停在 `1,048,500/1,048,576`，差 76 字节），逐字节全覆盖判定会让 RAR 分卷**永远删不掉**。
  现在的判据是 `covered >= length - 4096` **且** handler 已经问过更靠后的卷。
- `_highestVolumeServed` 在 **Open 阶段**就会被推到最后一卷（枚举条目要读每卷的头），
  所以它单独**不能**作为"已经消费完"的证据 —— 必须叠加覆盖量。

### 验证

`tests/SliceArc.Probe` **第 11 节**（本机有 `C:\Program Files\WinRAR\Rar.exe` 就造真包测，没有就跳过）：

- 11.1 多卷 RAR 造出来 + 识别为 `RarNewStyle` / `IsIndependentVolumes`
- 11.2 解压成功 + 逐字节 SHA256 一致
- 11.3 **`Build.25778117.part1.exe`（`-sfx -v1m`）被当作分卷组，解压内容正确**
- 11.4 **边解边删回收 N-1 卷**（4 卷删 3 留 1）+ 产物仍正确
- 失败时会打印每卷 `served/spent/covered` 状态，直接看出是哪一环没成立

### ⚠️ 独立卷**不能**边解边删（2026-10-13 真实事故 + 实测复现）

**这是本文件里最容易踩死人的一条。** 切片卷可以边解边删，独立卷绝对不行。

事故现场：用户解一个 **17 卷 × 4GB（Rar5）** 的包，勾了「解压后删除已用完的分卷」。结果：

```
提示：解压中断 / 可能已损坏、不完整
实际：文件基本都出来了
但  : 只剩 part01/02/15/16/17 —— 03~14 被删了，后面的卷没删干净
```

**根因链**（每一环都实测过）：

1. RAR 的每一卷都是**独立归档**，解码器持有**每卷各一个流**，而且**解压过程中还会回头读**
   （每个成员的头、以及卷尾部记录，都在它当初被写下的那一卷里）。
2. 边解边删把某一卷删掉 → 那次回头读失败 → `hr != S_OK`
   → `ExtractionEngine` 走 `RecordFailure("解压中断", "压缩包可能已损坏或不完整…")` → 结果 `Ok=false` → 弹警告。
3. 中断导致**后面的卷没被读完** → 永远不满足"已消费" → 删不掉（"好几个分卷没删干净"）。
4. 前两卷（part01/02）在中途被跳过的原因是另一条：
   解码器刚开档时扫过它们，解压时**又回到偏移 0** 重读 → `IsDeletionSafe=false`（有回跳）→ 中途不删。

**亲手复现（可重复）**：解压一个 69 卷的 RAR 时，手动删掉还没轮到的一卷：

```powershell
$p = Start-Process $exe -ArgumentList @('--extract', "<part01>", '--out', "<out>", '--quiet') -PassThru
Start-Sleep -Milliseconds 250
Remove-Item "<...part30.rar>"        # 解码器还没读到，但后面必读
$p.WaitForExit()
```

→ `exit=1`，日志：`解压 3 项 · 失败 1 项 · 原因：数据损坏 · 建议：…缺失或不完整的分卷…`

**正确行为（已实现）**：

- **独立卷只在整包解压全部成功之后统一回收**（`!final && IsIndependentVolumes` 直接 return）。
- 全量成功时用「整个集合都已消费」而不是逐卷覆盖判定 —— 因为解码器**根本读不到卷尾最后 ~76 字节**
  （结构记录），只含归档终止记录的尾卷甚至完全不读；按覆盖判定的话"边解边删"只会删掉一部分。
- 单卷删除失败只记 warning，**不许**把整次解压判成失败（原来的写法会让完成对话框引导用户去怀疑压缩包损坏）。
- `Extract` 抛异常时也要跑一次最终回收（独立卷模式下那是唯一的回收点）。
- 切片卷（`.7z.001` / `.z01`）行为不变，仍然是真·边解边删。

**回归保护**：Probe **11.5**（独立卷中途删除必须为 0 次）、**11.6**（切片卷必须仍然中途删除）。

### 顺带修掉的一个老 bug：Windows 文件枚举不支持字符类

`TryRarOldStyle` 原本用 `Directory.EnumerateFiles(dir, baseName + ".[rs]*")` 找 `.r00/.s00` 兄弟卷。
**Windows 的 FindFirstFile 通配符只认 `*` 和 `?`，`[rs]` 被当成字面量** —— 于是老式 RAR 分卷
（`.rar + .r00 + .r01`）**从来没被识别过**，一直悄悄退化成"单文件"。改成枚举 `baseName + ".*"`
再交给正则过滤即可。新增的 11.0 断言（不需要 WinRAR 也能跑）就是盯这个的。

---

## 七、参考

- 源码位置：`.refs/src/CPP/7zip/`（7-Zip 26.04 官方源码，已下载解压）
- 关键文件：`IDecl.h`（GUID 规则）、`IStream.h`、`IProgress.h`、`PropID.h`、
  `Archive/IArchive.h`（接口 + 枚举）、`Archive/Rar/RarHandler.cpp`（卷回调）、
  `Archive/Rar/RarVol.h`（卷名推导）
- 验证程序：`tests/SliceArc.Probe/Program.cs`
