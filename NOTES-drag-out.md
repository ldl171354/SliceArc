# SliceArc — 拖拽直出（零临时文件）设计与踩坑

> 2026-10-12 实现并实测。协议层由 `tests/SliceArc.Probe` 第 9 节全自动验证；
> 真实鼠标拖拽这一步**没有**机器验证手段（见第六节，如实标注）。
> 环境：Windows 11 / .NET 8 / x64。

---

## 一、这东西到底解决了什么

用户痛点原话：Bandizip 拖拽会把整个包先解到 `%TEMP%`，而 C 盘只剩 12GB。

Windows 的拖放协议本身就是「数据源按需供给」，所以可以做到**全程不落盘**：

```
我们的进程 (IDataObject)                  explorer.exe / 目标窗口
   │  ← QueryGetData(FileGroupDescriptorW)     先问：你给什么？
   │  → S_OK
   │  ← GetData(FileGroupDescriptorW)          要清单
   │  → HGLOBAL: FILEGROUPDESCRIPTORW（文件名+大小+时间）
   │  ← GetData(FileContents, lindex=i)        第 i 个文件的内容
   │  → STGMEDIUM{ TYMED_ISTREAM, IStream* }
   │  ← IStream::Read(buf, n)  ←────────────┐  explorer 现场建目标文件
   │     └→ 后台 7z.dll 解码 → 有界队列 ─────┘ 直接写进最终路径
   └─ 全程零临时文件、零额外磁盘占用
```

关键点：**字节流向是 explorer 拉、我们推**。我们从不打开目标文件，目标路径由 shell 决定，
所以我们既不需要知道它、也不可能去写它——这是「零临时文件」的物理保证，不是靠自觉。

---

## 二、为什么必须自己实现 COM `IDataObject`

WPF 有 `System.Windows.DataObject`，但它表达不了「虚拟文件」：`DataFormats.FileDrop`
要求真实路径，`SetData` 塞 Stream 也不支持 `CFSTR_FILECONTENTS` 的 **lindex 语义**
（同一个格式，按索引返回不同文件的内容）。所以只能自己实现 COM 接口，
用 `ole32!DoDragDrop` 起拖拽循环。

实现清单（`src/SliceArc.Core/DragDrop/`）：

| 类型 | 作用 |
|---|---|
| `VirtualFileDataObject : IOleDataObject` | 描述符 + 按索引产出 IStream |
| `VirtualFileStream : IOleStream` | 把 `ArchiveEntryReader` 包装成 IStream（Read/Seek/Stat） |
| `FormatEnumerator : IEnumFORMATETC` | 列出 3 种格式（描述符 / 内容 / 首选效果） |
| `SliceArcDropSource : IDropSource` | Esc 取消、松开鼠标放下、用系统默认光标 |
| `DragOut.DoDragDrop` | `OleInitialize` + `GetComInterfaceForObject` + `ole32!DoDragDrop` |

所有接口**扁平声明、全 `IntPtr` 出参、全 `[PreserveSig]`**，与 7-Zip 那套同一规矩。

---

## 三、字节从哪来：OutputFactory + 有界队列

`ExtractionEngine` 原本只会写文件，为了拖拽加了一个可插拔出口：

```csharp
public Func<ArchiveEntry, Stream>? OutputFactory { get; init; }
```

- 设了它，`GetStream` 就不再解析输出路径、不管覆盖策略，直接把 7z.dll 的字节写进这个 Stream；
- 引擎在条目结束时 `Dispose()` 这个流；
- 引擎**不再创建输出目录**（所以拖拽连一个空目录都不会留）。

`ArchiveEntryReader` 是消费者侧：

```
Task.Run(Produce)  ──  ExtractionEngine.Extract(IncludePaths=[该条目], OutputFactory=队列)
                              │ Write(256KB 块)
                              ▼
                        ByteQueue（上限 8MB，满了就阻塞解码线程 = 背压）
                              │ Read()
                              ▼
                        IStream::Read  ←── shell 的拷贝引擎
```

- **背压是核心**：7z.dll 不能被暂停，所以唯一能在不把 40GB 灌进内存的前提下服务慢速读取者的办法，
  就是队列满了直接阻塞解码线程。
- **前向 seek** 靠丢弃（边读边丢）；**后向 seek** 重启解码（正确但慢，shell 自己的拷贝循环不会用到）。
- 进度回调按 100ms 节流：否则 40GB 文件会把 UI 线程的 Dispatcher 淹掉。

---

## 四、生命周期：`DoDragDrop` 返回 ≠ 拷贝结束

这是最容易写错的地方。**成功放下之后，shell 仍在后台拉字节**，而且这些调用是在
`DoDragDrop` 返回**之后**才到达的（跨进程标准封送 → 回到我们的 STA → 靠 UI 线程继续泵消息）。

所以：

```csharp
var effect = DragOut.DoDragDrop(dataObject);
if (effect == 0) dataObject.Dispose();            // 取消：立刻拆掉，解码线程也停
else dataObject.MarkDragFinished(cancelled: false); // 成功：等它自己回收
```

`MarkDragFinished` 起一个 500ms 的定时器，**所有 reader 都结束**（或 10 分钟兜底）才真正 Dispose。
立刻 Dispose 的后果是拷贝中途 `IStream::Read` 失败 → 用户拿到一个半截文件。

> 顺带：`Dispatcher.BeginInvoke` 在 OLE 的模态拖拽循环里仍然会被处理（WPF 的 Dispatcher 消息钩子
> 在 `DispatchMessage` 路径上），所以拖拽过程中的进度条是活的。

---

## 五、拖出**永不**删分卷

`ArchiveEntryReader` 里写死了 `DeleteSpentVolumes = false`，理由是数据安全：

一次拖拽只读**一个**条目，而分卷消费判定是「`[0, 卷末尾)` 被覆盖」——如果拖的是包里最前面的
小文件，第 0 卷可能被判为「已用完」而被删。用户下一分钟想再拖第二个文件时，归档已经残废了。

删卷只在**整包解压**（主窗口/右键菜单）里发生，那里用户是明确要求「用完一卷删一卷」的。
Probe 9.11 就是这条的回归断言：拖完 4 卷包后，4 卷必须全在。

---

## 六、验证到什么程度（如实）

### 自动验证（Probe 第 9 节，全绿）

| 断言 | 证明了什么 |
|---|---|
| 9.2 描述符 4+584×N、cItems、名字、大小、`dwFlags=0x64`、子目录相对路径 | `FILEGROUPDESCRIPTORW` 布局对 |
| 9.3 `Marshal.QueryInterface` 命中正确 IID、错 IID 落空 | IDataObject 的 GUID 对 |
| 9.3 slot4 返回 `E_NOTIMPL`（而 slot3 是 GetData） | **vtable 槽位顺序对** |
| 9.4 `GetData` 返回 HGLOBAL，`GlobalSize` 与内容逐字节一致 | 介质类型与分配对，释放交给 `ReleaseStgMedium` |
| 9.5 `EnumFormatEtc` 返回 3 种格式（S_FALSE 结束） | 枚举器可用 |
| 9.6/9.7 `TYMED_ISTREAM` + slot4=Write(E_NOTIMPL) + slot12=Stat | IStream 槽位顺序对、Stat 结构写全 |
| 9.8 全量读 + SHA256 与原始一致（512KB 与 1MB 两个文件） | 流的内容对 |
| 9.9 回退 seek 后重读一致 | 重启解码路径对 |
| 9.11 4 卷包拖出内容正确、4 卷仍在 | 拖拽不删卷 |
| 9.12 工作目录文件清单拖拽前后**完全一致** | **零临时文件** |

### 没有验证的一步

**真实鼠标拖拽**（在窗口里按住文件拖到资源管理器）没有自动化手段：
- 合成鼠标输入（`SendInput`）会真的动用户的鼠标、真的和桌面交互，代价远大于收益；
- 而 `DoDragDrop` 是模态循环，程序化调用在没有鼠标输入时会一直等消息，没法当自检跑。

所以链路上「`DoDragDrop` 真的把拖拽交给系统、资源管理器真的按描述符建目录树」这一段
只有协议层保证，**没有实测**。第一次手动拖拽时请留意两件事：
1. 拖单个文件 → 目标目录出现完整文件；
2. 拖一个目录 → 相对路径里的子目录由 shell 现场创建（Windows 自带的「压缩文件夹」拖拽就是这个机制）。

---

## 七、踩坑（都不是 7-Zip 那侧的）

### 坑 1：进程内调自己的 CCW，`GetObjectForIUnknown` 会还你**托管对象**

```csharp
var ptr = Marshal.GetComInterfaceForObject(obj, typeof(IOleDataObject));
var caller = (IDataObjectCaller)Marshal.GetObjectForIUnknown(ptr);   // InvalidCastException
```
CLR 的身份表认出这是自己的 CCW，直接返回原托管对象 → 转换失败，**vtable 根本没被碰过**。
所以「用 RCW 验证 vtable」这件事在进程内是假的。

正确做法：自己读 vtable 槽位再调用：

```csharp
var vtable = Marshal.ReadIntPtr(comPointer);
var slot3  = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);   // 0=QI,1=AddRef,2=Release
var getData = Marshal.GetDelegateForFunctionPointer<GetDataDelegate>(slot3);
```
配合「slot4 必须返回 E_NOTIMPL」这种**行为可区分**的断言，才能证明槽位顺序。
（见 `tests/SliceArc.Probe/OleCallers.cs`）

### 坑 2：`STGMEDIUM` / `STATSTG` 必须整体写

`STATSTG.pwcsName` 是 shell 会读的字段。只写 `type`/`cbSize` 而把其余留在栈上垃圾里，
shell 可能去 free 一个野指针。用 `Marshal.StructureToPtr(整个结构, ...)`，不要逐字段 `WriteInt32`。

### 坑 3：`FILEDESCRIPTORW` 是 584 字节，名字在偏移 64，最多 259 字符

```
0   dwFlags(4)   4 clsid(16)   20 sizl(8)   28 dwFileAttributes(4)
32  ftCreation(8)  40 ftLastAccess(8)  48 ftLastWrite(8)
56  nFileSizeHigh(4)  60 nFileSizeLow(4)  64 cFileName(260 WCHAR)
```
- `dwFlags = FD_ATTRIBUTES|FD_FILESIZE|FD_WRITESTIME = 0x64`
- `cFileName` 可以带 `\` 相对路径，shell 会建中间目录（Windows 自带压缩文件夹拖拽就靠这个）
- 整个 `FILEGROUPDESCRIPTORW` = `4 + 584×N`，用 `GlobalAlloc(GMEM_MOVEABLE)` 分配，
  **释放交给 shell 的 `ReleaseStgMedium`**（它会 `GlobalFree`），我们不要自己 free

### 坑 4：`CFSTR_FILECONTENTS` 的 `lindex` 是「第几个文件」

描述符里的顺序就是 lindex 的顺序。`QueryGetData` 也要按 lindex 判合法性，
否则 shell 在 `DragEnter` 阶段就会判定「这个源给不了文件」。

### 坑 5：`CoTaskMemFree` 在 ole32、数组参数要 `LPArray`

与本项目其它 COM 互操作同源（见 `NOTES-shell-integration.md` 第四节）。

---

## 八、已知代价 / 后续优化

- **固实包多文件拖拽是 O(n²)**：每个流一个独立解码，N 个文件就解码 N 次固实块。
  拖一个文件（最常见）是最优的。要优化就得让一个解码会话同时喂多个队列——
  但 shell 可能乱序读，共享生产者会死锁（生产者卡在文件 A 的满队列上，消费者在等文件 B）。
  真要做，得让生产者对「还没人读的条目」直接丢弃，代价是那些条目要重启解码。
- **空目录不会还原**：描述符只列文件。要还原空目录得额外塞 `FILE_ATTRIBUTE_DIRECTORY` 条目。
- **进度是每个流各自的**，多文件拖拽时进度条会在文件之间跳。
- 目前只给 `DROPEFFECT_COPY`（不提供 MOVE 语义），拖到「移动到」场景仍按复制处理。
