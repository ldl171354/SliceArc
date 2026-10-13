# SliceArc — 交接文档（HANDOFF）

> **给下一个接手的人或 AI：先读完这一页，再读 `NOTES-7z-com-interop.md`（7z.dll COM 层）、
> `NOTES-shell-integration.md`（右键菜单 + 命令行契约）、`NOTES-drag-out.md`（拖拽直出），
> 然后就能接着干，不用重新推导任何东西。**
> 最后更新：2026-10-12 · 本轮补完了 `ExtractionEngine`、整个 WPF UI、原生右键菜单，
> 以及**拖拽直出（零临时文件）**。两个核心卖点都已落地并实测。

---

## 0. 这是什么

Windows 上的**分卷压缩包解压器**。核心卖点两个：

1. **边解压边删已完成的分卷** —— 用户的核心场景，解压 50 卷的包时逐卷回收空间（**已实现并实测**）
2. **拖拽直写** —— 把文件从窗口直接拖到资源管理器，边解码边写入目标，**全程零临时文件**（**已实现**，
   协议层全自动验证，见第 6 节）

技术栈：**C# / .NET 8 / WPF**，解码全靠 7-Zip 官方的 `7z.dll`（**不自己写任何格式解析器**）。

交互已拍板（用户原话：「解压前勾选就自动删除即可，不要搞那么复杂，主要目标是功能强大随时能用」）：

- 主界面 = **Bandizip 风的极简窗口**：一行工具栏 + 一行地址栏 + 一个列表 + 一行状态栏，没有向导、没有多步配置
- 删除策略 = 工具栏上一个复选框「解压后删除已用完的分卷」+「保留最后 N 卷」保险丝
- 右键 = **资源管理器原生菜单项**（HKCU 静态动词，免管理员，**平铺、无子菜单**）：
  「SliceArc：解压到当前文件夹 / 解压到「归档名」文件夹 / 解压并删除已用完的分卷 /
  测试压缩包完整性 / 用 SliceArc 打开」
  ⚠️ **别改回级联**：`SubCommands=""`、`ExtendedSubCommandsKey` 三种写法在本机全部渲染成
  「有子菜单但里面是空的、点了没反应」。详见 `NOTES-shell-integration.md` 第二节。

---

## 1. 位置与环境

| 项 | 值 |
|---|---|
| 项目根 | `D:\SliceArc`（**故意放 D 盘**，C 盘只剩 12GB） |
| .NET SDK | 8.0.425（已装） |
| 7-Zip | **26.04，已装** → `C:\Program Files\7-Zip\7z.dll` |
| 7-Zip 源码参考 | `D:\SliceArc\.refs\src\CPP\7zip\`（GUID / 接口签名的权威出处，别删） |
| 发布产物 | `D:\SliceArc\dist\SliceArc.App.exe`（framework-dependent，本机有 SDK 直接能跑） |
| 平台 | x64 only（`PlatformTarget=x64`，COM 互操作需要；App 与 Core 都要） |

```powershell
cd D:\SliceArc
dotnet build SliceArc.sln -c Release                                    # 必须 0 错误 0 警告
dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -o D:\SliceArc\dist
```

**依赖只有 7z.dll 一个**，通过 `NativeLibrary.SetDllImportResolver` 动态定位，优先找应用目录，
回退到 `Program Files\7-Zip\`。

> ⚠️ `BuiltInComInteropSupport=true` 必须**同时**出现在 `SliceArc.Core.csproj` 和 `SliceArc.App.csproj`：
> CCW/RCW 的开关是**进程级**的，Core 是库，最终进程由 App 决定。

---

## 2. 现在能跑什么（已实测，不是推测）

### 2.1 Core / 引擎 / 注册表 / shell 可见性

```powershell
cd D:\SliceArc
dotnet run --project tests\SliceArc.Probe\SliceArc.Probe.csproj -c Release
```

2026-10-12 实测：**第 1–9 节全部通过**（`===== 全部通过 =====`）。

| 节 | 内容 |
|---|---|
| 1 | 7z.dll 解析 + 60 个格式处理器枚举 |
| 2–3 | 用 `7z.exe a -mx1 -v1m` 造 4 卷包；分卷组识别 / 风格 / 序号 / 总字节 |
| 4–5 | CCW 关键验证（`IInArchive::Open`/`Extract`）+ 分卷消费追踪（覆盖集推到 EOF） |
| 6 | **`ExtractionEngine` 端到端**：全量解压 + SHA256、精确路径选择、通配符、不覆盖跳过、测试模式、**路径穿越防护**、**边删边解（6 卷删 5 留 1）**、目录扫描折叠分卷、非归档明确拒绝 |
| 7 | **右键菜单注册表往返**（隔离在 `HKCU\Software\SliceArc.Probe\Classes`，测完自动清理） |
| 8 | **资源管理器到底会不会显示**：用 shell API 读回真实右键菜单 → 压缩包上出现 `SliceArc`（`hasSubmenu=True`），普通文件上不出现（`AppliesTo` 生效） |
| 9 | **拖拽直出协议**：`FILEGROUPDESCRIPTORW` 布局、IDataObject/IStream 的 **IID 与 vtable 槽位顺序**、HGLOBAL 介质、`EnumFormatEtc`、整文件 SHA256、回退 seek、**拖拽后分卷仍在**、**零临时文件** |

> 第 8 节需要先 `--register`，否则自动跳过。
> 输出末尾可能混入 `SharedMemory read faild` —— 那是本机第三方 shell 扩展（迅雷/OPPO 互联）的噪声，与本项目无关。

### 2.2b 拖拽直出（`NOTES-drag-out.md` 有完整设计）

在窗口里按住列表中的文件/目录拖到资源管理器即可。归档视图走**自实现的 COM IDataObject**
（虚拟文件，零临时文件）；文件夹视图走系统原生 `FileDrop`。

**已自动验证**：描述符字节、IID、vtable 槽位顺序（slot4 返回 E_NOTIMPL 这种可区分行为）、
IStream 槽位（3=Read/4=Write/12=Stat）、两个文件的 SHA256、回退 seek、拖完分卷仍全在、
以及**工作目录拖拽前后文件清单完全一致**。

**没有验证**：真实鼠标拖拽本身。`DoDragDrop` 是模态循环，程序化调用没有鼠标输入就会一直等消息，
合成鼠标又会真动用户的桌面，所以这段只能靠协议层保证。第一次手动拖的时候留意：
单文件要完整出现；拖目录时相对路径里的子目录由 shell 现场创建。

### 2.2 右键菜单的命令行（= 真的会执行的那条命令）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\cli-e2e-test.ps1
```

实测全部通过：造 6 卷包 → `--extract --to-subfolder --delete-spent --quiet` → 产物 SHA256 一致、
**5 卷被回收、只剩最后一卷**（保险丝生效）→ `--extract-all --here` 批量 → `--test` 校验。

### 2.3 UI 真的画出来了吗

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\inspect-ui.ps1 -Target <某个分卷.001>
```

用 UI Automation 遍历运行中的窗口（比截图更可靠，因为本机没配视觉模型）。实测 61 个控件：
工具栏 7 个按钮、2 个复选框、「保留 + 下拉 + 卷」、地址栏、**6 列表头**、
数据行（`inner.bin / 2 MB / 2 MB / BIN 文件 / 2026-10-12 13:27 / CRC 4D2C85AA`）、状态栏两段文本。

`tools\capture-ui.ps1` 也能截图（`D:\SliceArc\artifacts\ui-main.png`），但本机 modlens 没配视觉 provider，看不了。

### 2.4 右键菜单当前状态

```
HKCU\Software\Classes\*\shell\SliceArc  →  D:\SliceArc\dist\SliceArc.App.exe
D:\SliceArc\dist\SliceArc.App.exe --shell-status     # 打印注册状态 + 全部动词
D:\SliceArc\dist\SliceArc.App.exe --unregister       # 一键回滚
```

---

## 3. 代码地图

### Core（`src/SliceArc.Core`）

| 文件 | 职责 |
|---|---|
| `Volumes/VolumeDetector.cs` | 分卷组识别。4 种命名风格：`.7z.001` / `.part1.rar` / `.rar`+`.r00` / `.z01..zip`。算出每个分卷在**逻辑归档流**中的字节区间 |
| `SevenZip/SevenZipNative.cs` | 7z.dll 的 P/Invoke、接口 GUID、PROPID、VARTYPE、HRESULT、`PropVariant` |
| `SevenZip/SevenZipInterfaces.cs` | COM 接口定义。**全部扁平声明，绝不继承**（见第 5 节坑 1） |
| `SevenZip/VolumeAwareStream.cs` | **全案核心**。多个物理分卷伪装成一个连续流 + 记录每字节读过没有。`ResetCoverage()` 只在「换处理器重试」时用 |
| `SevenZip/SevenZipLibrary.cs` | 加载 7z.dll、枚举 60 个格式、按扩展名找 handler、`CreateObject` |
| `Extraction/ExtractionModels.cs` | `ArchiveEntry` / `ExtractionOptions` / `ExtractionProgress` / `ExtractionResult` |
| `Extraction/ExtractionEngine.cs` | **核心**。`Open()`（扩展名提示 → 全格式回退）+ 条目枚举 + `Extract()` + `Test()` + 路径穿越防护 + zip 炸弹上限 + 取消 + 边解边删 + 节流进度回调 + **可插拔输出（`OutputFactory`）** |
| `Extraction/ArchiveEntryReader.cs` | **拖拽直出的字节来源**：后台解码单个条目 → 有界队列 → 供 IStream 拉取（背压、前向丢弃、后向重启） |
| `Extraction/ByteQueue.cs` | 有界阻塞字节队列（队列满就阻塞解码线程 = 背压） |
| `Extraction/ArchiveScanner.cs` | 目录扫描：把 N 个分卷折叠成 1 个归档候选（给批量解压和文件夹视图用） |
| `DragDrop/OleInterop.cs` | 手写 OLE 面：FORMATETC/STGMEDIUM/STATSTG + IDataObject/IStream/IEnumFORMATETC/IDropSource + ole32/kernel32 P/Invoke |
| `DragDrop/VirtualFileDataObject.cs` | 虚拟文件拖拽：描述符 HGLOBAL、按 lindex 产出 IStream、格式枚举、DropSource、`DoDragDrop` 包装、**延迟自释放** |
| `Shell/ShellMenuRegistrar.cs` | HKCU 右键菜单安装 / 卸载 / 读回（`Inspect()`），`Register(options, basePath)` 供测试隔离 |

### App（`src/SliceArc.App`）

| 文件 | 职责 |
|---|---|
| `App.xaml(.cs)` | 入口分发：`--extract` / `--extract-all` / `--register` / `--unregister` / `--shell-status` / 无参 → 主窗口 |
| `Themes/Minimal.xaml` | 整套视觉：调色板 + 按钮/复选框/进度条/滚动条/DataGrid 模板（全部手写，不用系统默认控件外观） |
| `Views/MainWindow.xaml(.cs)` | 主窗口：工具栏、地址栏、列表（**压缩包内浏览**或**磁盘文件夹浏览**两种模式）、内联进度、状态栏、拖放、**拖出（归档=虚拟文件 / 文件夹=原生 FileDrop）**、排序、筛选、右键上下文菜单 |
| `Views/ProgressWindow.xaml(.cs)` | 右键菜单用的紧凑进度窗（一行上下文 + 一条进度 + 取消），成功自动关闭 |
| `Models/BrowserRow.cs` | 列表行（不可变）+ `ArchiveSummary` |
| `Cli/CommandLine.cs` | 命令行解析 + `Usage` |
| `Services/ExtractionRunner.cs` | CLI → `ExtractionOptions`；`OutputNaming`（`sample.7z.001` → `sample\`） |
| `Services/AppLog.cs` | 便携日志（exe 同目录 `SliceArc.log`，512KB 轮转）+ 附加父控制台（按控制台代码页编码） |
| `Services/AppSettings.cs` | 便携设置（exe 同目录 `SliceArc.settings.json`），**不写 C 盘** |
| `app.manifest` | asInvoker + PerMonitorV2 + longPathAware |
| `Assets/SliceArc.ico` | 9 个尺寸的 PNG-in-ICO，由 `tools/make-icon.ps1` 生成 |

### 工具（`tools/`）

| 脚本 | 用途 |
|---|---|
| `make-icon.ps1` | 生成多尺寸 `.ico`（System.Drawing 画，无外部依赖） |
| `cli-e2e-test.ps1` | 完全按右键菜单的命令行跑端到端（造包→解压→校验→删卷→批量→测试） |
| `inspect-ui.ps1` | UI Automation 遍历窗口控件树，验证 UI 真的渲染出来了 |
| `capture-ui.ps1` | 截图（PrintWindow，被遮挡也能拍） |

### 未完成 ❌

| 要做的东西 | 说明 |
|---|---|
| 归档内右键「解压选中项到…」的默认目录记忆 | 现在固定用「上次用过的目录」 |
| 多实例合并（第二次右键复用已有窗口） | 现在每个右键动作一个进程，各自弹进度窗；够用但不优雅 |
| 加密包口令输入框 | 引擎支持 `Password`，UI 没入口；遇到加密包目前直接报「wrong password」 |
| 拖拽的固实包优化 | 多文件拖拽目前每个流独立解码（O(n²)）；见 `NOTES-drag-out.md` 第八节 |
| 拖拽还原空目录 / 提供 MOVE 语义 | 描述符只列文件；只给 `DROPEFFECT_COPY` |

---

## 4. 下一步建议（按优先级）

1. 加密包口令输入（`ExtractionEngine` 已经支持，只差一个密码框 + 失败重试）
2. 拖拽的固实包单会话多流优化（真要做，注意 shell 乱序读会死锁，见 NOTES 第八节）
3. 「解压后删除分卷」的**干跑预览**：解压前告诉用户「预计回收 X GB」，比一个复选框更让人放心
4. 拖拽还原空目录 + `DROPEFFECT_MOVE` 语义

---

## 5. 三个绝对不能踩的坑（7z.dll COM 层）

**完整版在 `NOTES-7z-com-interop.md`，这里只给结论。**

### 坑 1：COM 接口必须**扁平声明**，不能靠 C# 接口继承
`interface IInStream : ISequentialInStream` → `Fatal error. Internal CLR error. (0x80131506)`。
把全部槽位平铺写在一个接口里（含 `IProgress` 的两个槽位）。

### 坑 2：所有输出参数用 `IntPtr`，不能用 `out`
`out uint processedSize` → `E_POINTER`。另外 `IInArchive.Open`/`Extract` 的接口参数要传**裸指针**
（`Marshal.GetComInterfaceForObject`），不能用 `[MarshalAs(UnmanagedType.Interface)]`。

### 坑 3：分卷"已消费"判定必须用**区间覆盖集**，不能用最大偏移
判据：`[0, 卷末尾)` 被**单个连续区间**覆盖。`BeginExtractionTracking()` **绝不能**清空 `_covered`。
（补充：`ResetCoverage()` 是另一回事，只在「某个处理器被 7z.dll 拒绝后换下一个重试」时调用，
否则失败尝试读过的字节会让所有分卷误判为可删。）

### 坑 4（本轮新增）：`IArchiveExtractCallback` 的协议细节
- 顺序是 `GetStream → PrepareOperation → (Write…) → SetOperationResult`
- **每个条目都会走完这四个回调**，包括被跳过的：`GetStream` 不给出流 → 7-Zip 把 askMode 改成
  `kSkip` 再调 `PrepareOperation(kSkip)` → `SetOperationResult(ok)`。所以**不能用「SetOperationResult 被调用」当作「解压成功」**，要看 `PrepareOperation` 记下的模式 + 自己有没有开过文件。
- `testMode=1` 时目标条目的 askMode 是 `kTest`：不写文件，但解码器仍然解码并校验 CRC —— 这就是 `Test()` 的实现方式。
- `Extract(indices, numItems, …)` 传显式索引时，`numItems == 0xFFFFFFFF` 才是"全部"；索引必须升序。
- 进度的 `SetCompleted` 是**按解码块**报的，可能超过成员大小之和；解压模式下用输出流的字节数当进度，只有测试模式才用 `SetCompleted`。

---

## 6. 拖拽直写（已实现）

**完整设计、踩坑、验证边界都在 `NOTES-drag-out.md`**，这里只给结论：

```
按住列表里的文件/目录拖到资源管理器
   explorer ← GetData(FileGroupDescriptorW) → HGLOBAL: FILEGROUPDESCRIPTORW
   explorer ← GetData(FileContents, i)      → STGMEDIUM{ TYMED_ISTREAM }
   explorer → IStream::Read(buf, n) ────────┐ explorer 现场建目标文件
        后台 7z.dll 解码 → 8MB 有界队列 ─────┘ 字节直接进最终路径
```

要点（每一条都踩过）：

1. **必须自实现 COM `IDataObject`**：WPF 的 `DataObject` 表达不了「按 lindex 返回不同文件内容」。
2. **字节来源**：`ExtractionOptions.OutputFactory` + `ByteQueue`（满了阻塞解码线程 = 背压）
   + `ArchiveEntryReader`（前向 seek 丢弃、后向 seek 重启解码）。
3. **`DoDragDrop` 返回 ≠ 拷贝结束**：成功放下后 shell 还在后台拉字节，
   所以数据对象要 `MarkDragFinished()` 延迟自释放（等所有 reader 结束或 10 分钟兜底），
   **取消**才立刻 Dispose。
4. **拖出永不删分卷**：一次只读一个条目，删卷会让后续拖拽打不开包（Probe 9.11 是回归断言）。
5. **拖拽期间输出目录连创建都不会发生**（`OutputFactory` 非空时引擎跳过 `CreateDirectory`）。

**没验证的一步**：真实鼠标拖拽本身（`DoDragDrop` 是模态循环，程序化调用会等消息；合成鼠标会真动桌面）。
协议层由 Probe 第 9 节全自动覆盖，含 vtable 槽位顺序与「工作目录文件清单前后完全一致」。

---

## 7. 验证方式

改完任何东西，**至少**跑前两条：

```powershell
cd D:\SliceArc
dotnet build SliceArc.sln -c Release                                     # 0 错误 0 警告
dotnet run --project tests\SliceArc.Probe\SliceArc.Probe.csproj -c Release   # 期望「全部通过」
```

涉及 UI / 右键 / 命令行时再加：

```powershell
dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -o D:\SliceArc\dist
D:\SliceArc\dist\SliceArc.App.exe --register
powershell -NoProfile -ExecutionPolicy Bypass -File tools\cli-e2e-test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\inspect-ui.ps1 -Target <分卷.001>
```

Probe 会自动用 `7z.exe a -t7z -mx1 -v1m` 造测试包（不可压缩随机数据），工作目录
`%TEMP%\slicearc-probe`，每次重跑自动清空。

**加新功能时，先往 Probe 里加断言再改 Core**——这套验证是唯一能防住 COM 层回归的东西。
（`dotnet build` 的 `[exit code: 1]` 常见于 PowerShell 把 stderr 上的诊断行当错误，
看输出里有没有「全部通过」才算数；诊断行现在默认关闭，`VolumeAwareStream.Diagnostics = true` 才打印。）

---

## 8. 用户背景（省得重新问）

- C 盘只剩 12GB，D 盘 97.6GB → **所有产物默认落 D 盘**（exe 同目录写日志/设置，不用 %APPDATA%）
- 已装 Bandizip + WinRAR + 7-Zip，习惯**窗口拖拽**和**右键菜单**
- 主要场景是**分卷压缩包**，希望「解压一个删一个」
- 已把 Bandizip 的临时目录改到 D 盘，但拖拽仍然走临时文件，所以需要这个工具
- 删除策略已拍板：**stream-safe + 保险丝**（分卷消费完才删；默认保留最后 1 卷；分卷不完整时**禁用**自动删除）
- 交互已拍板：**解压前勾选，勾了就自动删，不要复杂配置**
- 界面审美：**Bandizip 式简约**——一个工具栏、一个列表、一个状态栏，不要多步骤向导
- 屏幕 1536×960 @160% 缩放（WPF 逻辑尺寸只有 960×600），窗口默认尺寸要按工作区夹紧

---

## 9. 别做的事

- ❌ 不要硬编码 7-Zip 的 CLSID —— 用 `GetHandlerProperty2(i, kClassID)` 枚举
- ❌ 不要往 C 盘写任何东西（构建产物、临时文件、测试数据、设置都不行）
- ❌ 不要自己写 zip/7z/rar 解析器 —— 60 个格式全在 7z.dll 里
- ❌ 不要用 `out` 参数跨 COM 边界；COM 接口里的数组参数必须显式 `[MarshalAs(UnmanagedType.LPArray)]`
- ❌ 不要在 `BeginExtractionTracking()` 里清空 `_covered`（`ResetCoverage()` 另说，见坑 3）
- ❌ 不要把右键菜单改回级联（`SubCommands` / `ExtendedSubCommandsKey`）—— 本机实测三种写法都是空壳
- ❌ 不要在 `DoDragDrop` 返回后立刻 Dispose 虚拟数据对象 —— shell 还在后台拉字节
- ❌ 不要让拖拽路径去删分卷（`ArchiveEntryReader` 里 `DeleteSpentVolumes` 必须保持 false）
- ❌ 不要用「进程内 RCW 调用」去验证自己的 CCW vtable —— CLR 会直接还你托管对象（`NOTES-drag-out.md` 坑 1）
- ❌ 不要删 `.refs/src`（GUID 和接口签名的唯一权威来源）
