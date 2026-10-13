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

## 六、参考

- 源码位置：`.refs/src/CPP/7zip/`（7-Zip 26.04 官方源码，已下载解压）
- 关键文件：`IDecl.h`（GUID 规则）、`IStream.h`、`IProgress.h`、`PropID.h`、
  `Archive/IArchive.h`（接口 + 枚举）
- 验证程序：`tests/SliceArc.Probe/Program.cs`
