# SliceArc — 交接文档（HANDOFF）

> **给下一个接手的人或 AI：先读完这一页，再按需读
> `NOTES-7z-com-interop.md`（7z.dll COM 层 + 分卷两种布局）、`NOTES-shell-integration.md`（右键菜单 + 命令行契约）、
> `NOTES-drag-out.md`（拖拽直出）、`NOTES-add-to-archive.md`（向现有压缩包添加文件），
> 然后就能接着干，不用重新推导任何东西。**
> 最后更新：2026-10-13 晚 · 版本 **0.4.2**

---

## 0.0 给下一个 agent 的 30 秒速览

**当前版本 0.4.2。四个交付物都在 `dist\release\`：**

| 交付物 | 文件 |
|---|---|
| 公开版 安装包 | `dist\release\SliceArcSetup-0.4.2-x64.exe` |
| 公开版 便携版 | `dist\release\SliceArc-0.4.2-portable.zip`（解压后跑 `SliceArc.App.exe --register` 装右键） |

**本机自用**：右键菜单指向 `dist\app\SliceArc.App.exe`（自包含**文件夹**版，**不会往 C 盘解压**；
单文件版每次启动会往 `%TEMP%\.net\SliceArc.App\` 解压 ~280 MB）。

**跑一遍全部验证（6 套 + 1 套新加的 RAR 回收 E2E）：**

```powershell
cd D:\SliceArc
$App = 'D:\SliceArc\artifacts\bin\SliceArc.App.exe'
dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -o D:\SliceArc\artifacts\bin
dotnet run --project tests\SliceArc.Probe\SliceArc.Probe.csproj -c Release     # 12 节
powershell -NoProfile -ExecutionPolicy Bypass -File tools\cli-e2e-test.ps1        -App $App
powershell -NoProfile -ExecutionPolicy Bypass -File tools\rar-sweep-e2e-test.ps1  -App $App
powershell -NoProfile -ExecutionPolicy Bypass -File tools\add-e2e-test.ps1        -App $App
powershell -NoProfile -ExecutionPolicy Bypass -File tools\compress-formats-test.ps1 -App $App
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drag-add-e2e-test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\theme-smoke-test.ps1    -App $App
```

**最近一次事故（务必先读第 5 节坑 7）**：RAR 分卷**不能**边解边删 —— 解码器会回头读已读过的卷，
删掉就会报"数据损坏/不完整"并中断，导致后面的卷也删不掉。已改成"整包成功后才统一回收"。
切片卷（`.7z.001`/`.z01`）不受影响，仍然是真·边解边删。

---

## 0. 这是什么

Windows 上的**压缩 / 解压一体工具**（Bandizip 风格），解码与编码全靠 7-Zip 官方的 `7z.dll`
（**不自己写任何格式解析器**）。

核心能力：

1. **回收已完成的分卷** —— 切片卷（`.7z.001`/`.z01`）**边解压边回收**；
   RAR 这类**独立卷**在整包解压成功之后统一回收（原因见第 5 节坑 7，已实测）
2. **拖拽直写** —— 把文件从窗口直接拖到资源管理器，边解码边写入目标，**全程零临时文件**
   （协议层全自动验证，见第 6 节）
3. **压缩** —— 7z / zip / tar / gz / bz2 / xz / wim（zst 只能解不能压），支持分卷、加密、级别、方法
4. **向现有压缩包添加 / 更新文件** —— 7z / zip / tar / wim；按钮、**拖入窗口**、`--add-to` 三个入口
   （已实测，见 `NOTES-add-to-archive.md`）
5. **RAR 分卷**（含 `Build.part01.exe` 自解压首卷）—— RAR 是"独立卷"布局，
   必须靠 `IArchiveOpenVolumeCallback` 逐卷取，不能像 `.7z.001` 那样拼接
   （见 `NOTES-7z-com-interop.md` 第六节）

技术栈：**C# / .NET 8 / WPF**。

交互已拍板（用户原话：「解压前勾选就自动删除即可，不要搞那么复杂，主要目标是功能强大随时能用」）：

- 主界面 = **Bandizip 风的极简窗口**：一行工具栏 + 一行地址栏 + 一个列表 + 一行状态栏，没有向导、没有多步配置
- 主题 = **4 套色块调色板**（Light / Bandizip / Dark / Soft），设置弹窗里切换，持久化到 `SliceArc.settings.json`
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
| 7-Zip | **26.04，已装** → `C:\Program Files\7-Zip\7z.dll`（另有一份随包在 `src\SliceArc.App\7z.dll`） |
| 7-Zip 源码参考 | `D:\SliceArc\.refs\src\CPP\7zip\`（GUID / 接口签名 / update 契约的权威出处，别删） |
| 测试用构建 | `dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -o D:\SliceArc\artifacts\bin` |
| **日常使用（右键指向它）** | **`D:\SliceArc\dist\app\SliceArc.App.exe`** —— 自包含文件夹版，**不在 C 盘解压** |
| 分发用绿色版 | `dist\portable\SliceArc.App.exe`（单文件自包含，69 MB） |
| **交付物（0.4.2）** | `dist\release\` 下的 `SliceArcSetup-0.4.2-x64.exe`（安装包）与 `SliceArc-0.4.2-portable.zip`（便携版） |
| 打包脚本 | `tools\package.ps1 [-Installer]` → 绿色版 + ShellExt/MSIX + 安装包；**便携版 zip 要自己带 6 个附属文件**（见 2.6） |
| 平台 | x64 only（`PlatformTarget=x64`，COM 互操作需要；App 与 Core 都要） |

```powershell
cd D:\SliceArc
dotnet build SliceArc.sln -c Release                                    # 必须 0 错误 0 警告
dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -o D:\SliceArc\artifacts\bin

# 日常用（右键菜单指向这个）：
dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o D:\SliceArc\dist\app
```

> ⚠️ **别把右键菜单指向单文件版**（`-p:PublishProfile=Portable` 的产物）。它每次启动会把
> **~280 MB 原生库解压到 `C:\Users\17135\AppData\Local\Temp\.net\SliceArc.App\<构建哈希>\`**，
> 每个构建哈希一份、不会自动清理 —— 直接违反"C 盘只剩 12GB、不往 C 盘写东西"的前提。
> 单文件版只适合**分发给别人**；本机自用一律用上面的文件夹版（实测启动前后 `C:\Temp\.net` 字节数不变）。
> `tools\package.ps1` 产出的 `dist\portable` 就是单文件版，用它做分发、别拿它注册右键。

> ⚠️ **发布产物会过期**：`dist\` 里的包是某次打包的快照，源码改动后必须重新 publish 再测，
> 否则你测的是旧二进制（本轮就踩到一次：`dist\portable` 16:05，源码 17:57 还在改）。

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
| 11 | **RAR 分卷**（本机有 WinRAR 命令行时）：识别独立卷布局、解压 SHA256、**`.part1.exe` 自解压首卷**、**边解边删回收 N-1 卷** |
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

用 UI Automation 遍历运行中的窗口（比截图更可靠，因为本机没配视觉模型）。实测 103 个控件：
工具栏按钮（打开/压缩/添加/解压到…/快速解压/测试/…/设置）、复选框、地址栏、
**6 列表头**、数据行、状态栏两段文本。打开压缩包时「添加」可用、「压缩」禁用 —— 入口状态正确。

`tools\capture-ui.ps1` 也能截图（`D:\SliceArc\artifacts\ui-main.png`），但本机 modlens 没配视觉 provider，看不了。

### 2.3b 压缩格式矩阵（哪些格式真能创建）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\compress-formats-test.ps1 -App $App
```

实测结论（**能力是从 7z.dll 的 `kUpdate` 查出来的，不是写死的名单**）：

| 能创建 | 7z / zip / tar / wim（多条目容器）、gz / bz2 / xz（单流，一个文件） |
|---|---|
| **不能创建** | **zst** —— 7-Zip 26.04 只能读 zstd，**写不了**（`7z.exe a -tzstd` 自己就报 `System ERROR: 未实现`） |
| 完全不支持 | `.lz4` `.br` —— 随包 7z.dll 没有这两个 handler，既不能加也不能解压 |

所以：`--format zst` 会被明确拒绝并列出可创建格式；GUI 压缩窗里 `zst` 是**禁用项**
（`CompressionFormat.CanCreate()` 判定，UIA 实测显示「禁用 + 此版本 7z.dll 不支持创建」）。
`gz/bz2/xz` 是单流格式，**塞多个文件会自动打成 `.tar.gz`/`.tar.bz2`/`.tar.xz`**（7-Zip 的惯例）。

测试覆盖：四种容器格式「创建 + `7z.exe t` 校验 + 解压回来逐字节一致」、三种单流格式创建、
zst 的明确拒绝、以及无临时文件残留。

### 2.4 向现有压缩包添加文件（含真实拖拽投递）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\add-e2e-test.ps1 -App <测试构建的exe>
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drag-add-e2e-test.ps1
```

实测全部通过：

- **CLI 追加**：7z / zip / tar 的追加分支、保留分支、**同名替换分支**（条目数不增、内容是新的）；
  9 种不支持格式（rar/gz/bz2/xz/zst/lz4/br/lz/tar.gz）**原文件哈希与 mtime 完全不变、无临时文件残留**；
  分卷组拒绝且所有卷不变。
- **真实 OLE 拖拽**：拖单个文件、拖整个目录（含嵌套）、同名再拖一次（替换不重复）——
  全部用 **7z.exe 独立核对**归档内容与 SHA256。

设计与"怎么在没人拖鼠标的情况下真的测一次拖拽"（三个进程内 COM 身份表陷阱）
全在 `NOTES-add-to-archive.md`。

### 2.5 主题

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\theme-smoke-test.ps1
```

4 套调色板（Light / Bandizip / Dark / Soft）逐个启动主窗口：窗口成功创建且日志无
`XamlParseException` / 主题加载失败。调色板只覆盖 30 个**颜色**键，19 个**样式**键在
`Themes/Minimal.xaml` 里 —— 新增颜色时四套都要补，否则那个键会静默沿用 Light 的值。

### 2.6 怎么启动 / 右键菜单当前状态

**三种启动方式**：

1. **右键**（已注册，见下）
2. **双击** `D:\SliceArc\dist\app\SliceArc.App.exe` —— 打开主窗口
3. 命令行：`SliceArc.App.exe --help` 看全部开关

**右键菜单有两条并行通道，都已注册**：

| 通道 | 注册位置 | 在 Win11 上长什么样 |
|---|---|---|
| 经典静态动词 | `HKCU\Software\Classes\{*|Directory|Directory\Background}\shell\SliceArc*` | 藏在**「显示更多选项」**（Shift+F10）里 |
| **Win11 现代菜单** | 稀疏 MSIX `SliceArc.ContextMenu`（`Add-AppxPackage -ExternalLocation`） | **一级右键菜单里直接出现** |

两边的动词：解压到当前文件夹 / 解压到「归档名」文件夹 / 解压并删除已用完的分卷 /
测试压缩包完整性 / 用超速解压缩打开 / 压缩到 ZIP / 压缩到 7z / 压缩并分卷（50MB）。
解压类动词带 `AppliesTo`（只对压缩包显示），**压缩类动词对所有文件显示**（这是有意的）。

```
SliceArc.App.exe --shell-status     # 打印注册状态 + 全部动词 + 现代菜单状态
SliceArc.App.exe --register         # 注册（含现代菜单；需 exe 同目录有 ShellExt + msix + cer）
SliceArc.App.exe --unregister       # 一键回滚（含移除现代菜单包）
```

> 现代菜单需要 exe 同目录存在：`SliceArc.ShellExt.comhost.dll` / `.dll` / `.deps.json` /
> `.runtimeconfig.json` / `SliceArc.ContextMenu.msix` / `slicearc-sparse.cer`
> （`tools\package.ps1` 会把它们发布到 `dist\portable`；`dist\app` 是同样拷了一份）。
> 签名证书 `CN=SliceArc` 已在 `CurrentUser\TrustedPeople`，所以注册不需要管理员。
> 新装的包如果菜单没立刻出现，重启一次 explorer.exe。
> ⚠️ **做便携版 zip 时要把这 6 个文件一起打进去**，只给一个 exe 的话 `--register` 会报缺文件、
> 现代菜单装不上（`dist\release\*.zip` 就是这么打的，可照抄）。

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
| `Extraction/ArchiveVerifier.cs` | 批量校验（CRC）引擎，供「校验」工具窗用 |
| `Compression/CompressionEngine.cs` | 新建归档：7z/zip/tar/gz/bz2/xz/zst/wim，级别 / 方法 / 线程 / 分卷 / 加密 |
| `Compression/CompressionInternals.cs` | 源枚举（`CompressionSourceWalker`）与 handler 配置，被压缩与更新共用 |
| `Compression/ArchiveUpdateEngine.cs` | **向现有压缩包添加 / 更新文件**（update 模式）。同一 handler 先 Open 再 QI 出 `IOutArchive`，写临时文件后替换 |
| `Compression/ArchiveUpdateModels.cs` | `ArchiveUpdateOptions` / `ArchiveUpdateResult` / `UnsupportedArchiveUpdateException` |
| `DragDrop/OleInterop.cs` | 手写 OLE 面：FORMATETC/STGMEDIUM/STATSTG + IDataObject/IStream/IEnumFORMATETC/IDropSource + ole32/kernel32 P/Invoke |
| `DragDrop/VirtualFileDataObject.cs` | 虚拟文件拖拽：描述符 HGLOBAL、按 lindex 产出 IStream、格式枚举、DropSource、`DoDragDrop` 包装、**延迟自释放** |
| `Shell/ShellMenuRegistrar.cs` | HKCU 右键菜单安装 / 卸载 / 读回（`Inspect()`），`Register(options, basePath)` 供测试隔离 |
| `Shell/FileAssociationRegistrar.cs` | 文件关联（可选，默认不动系统） |
| `Shell/ShellPackageRegistrar.cs` | MSIX 稀疏包（Windows 11 新版右键菜单）注册 |

### App（`src/SliceArc.App`）

| 文件 | 职责 |
|---|---|
| `App.xaml(.cs)` | 入口分发：`--extract` / `--extract-all` / `--compress` / `--add-to` / `--register` / `--unregister` / `--shell-status` / 无参 → 主窗口；主题加载 |
| `Themes/Minimal.xaml` | 样式层（19 个控件模板 + Light 的 30 个颜色键） |
| `Themes/Palette.{Bandizip,Dark,Soft}.xaml` | 纯颜色覆盖层，运行时按 `settings.Theme` 合并 |
| `Views/MainWindow.xaml(.cs)` | 主窗口：工具栏、地址栏、列表（**压缩包内浏览**或**磁盘文件夹浏览**）、内联进度、状态栏、**拖入=添加到当前包**、**拖出（归档=虚拟文件 / 文件夹=原生 FileDrop）**、排序、筛选、设置弹窗 |
| `Views/CompressWindow` / `RenameWindow` / `VerifyWindow` / `AssociationsWindow` / `WelcomeWindow` / `ResultWindow` / `PasswordWindow` / `ProgressWindow` / `AboutWindow` | 各功能窗 |
| `Models/BrowserRow.cs` | 列表行（不可变）+ `ArchiveSummary` |
| `Cli/CommandLine.cs` | 命令行解析 + `Usage` |
| `Services/ExtractionRunner.cs` | CLI → `ExtractionOptions`；`OutputNaming`（`sample.7z.001` → `sample\`） |
| `Services/AppLog.cs` | 便携日志（exe 同目录 `SliceArc.log`，512KB 轮转）+ 附加父控制台（按控制台代码页编码） |
| `Services/AppSettings.cs` | 便携设置（exe 同目录 `SliceArc.settings.json`），**不写 C 盘** |
| `app.manifest` | asInvoker + PerMonitorV2 + longPathAware |
| `Assets/SliceArc.ico` | 9 个尺寸的 PNG-in-ICO，由 `tools/make-icon.ps1` 生成 |

### 测试（`tests/`）

| 工程 | 用途 |
|---|---|
| `SliceArc.Probe` | Core 层端到端：7z.dll/分卷/CCW/引擎/路径穿越/删卷/右键注册表/shell 可见性/**拖出协议**（第 1–9 节） |
| `SliceArc.DropProbe` | **进程内真实 OLE 投递**：构造真实 `MainWindow`，对它的 drop target 发 DragEnter/DragOver/Drop（见 `NOTES-add-to-archive.md` 第六节） |

### 工具（`tools/`）

| 脚本 | 用途 |
|---|---|
| `make-icon.ps1` | 生成多尺寸 `.ico`（System.Drawing 画，无外部依赖） |
| `cli-e2e-test.ps1` | 解压端到端：造 6 卷包 → 解压 → 校验 → **边解边删** → 批量 → 测试 |
| `rar-sweep-e2e-test.ps1` | RAR 分区回收：多卷/跨卷大文件/自解压首卷，断言中途删除为 0、成功后清干净 |
| `compress-formats-test.ps1` | 压缩格式矩阵：能创建的逐个验证（7z.exe 校验 + 逐字节回环），zst 必须被明确拒绝 |
| `add-e2e-test.ps1` | 追加/更新端到端：7z/zip/tar 三格式 × 追加/保留/替换 + 9 种不支持格式的安全拒绝 + 分卷拒绝 |
| `drag-add-e2e-test.ps1` | **真实 OLE 拖拽投递**：拖文件 / 拖目录 / 同名替换，7z.exe 独立核对 |
| `theme-smoke-test.ps1` | 4 套主题逐个启动窗口，验证调色板能加载 |
| `inspect-ui.ps1` | UI Automation 遍历窗口控件树，验证 UI 真的渲染出来了 |
| `capture-ui.ps1` | 截图（PrintWindow，被遮挡也能拍） |
| `package.ps1` / `SliceArc.iss` | 打包（绿色版 + Inno Setup 安装包） |

### 未完成 ❌

| 要做的东西 | 说明 |
|---|---|
| 右键「添加到压缩包」动词 | 要改注册表 + 重注册装机脚本；当前只有主窗口按钮 / 拖入 / CLI |
| 分卷压缩包的追加 | 明确不支持（会同时重建一组卷，风险高）；已给中文提示 |
| RAR 分卷的**中途**回收 | 现在是"整包成功后统一回收"（安全优先）。想做真·边解边删，得能证明解码器不会回读某卷 —— 可考虑记录每卷被回跳访问的区间，或用 `IArchiveOpenVolumeCallback` 之外的信息来判断，**但必须先用大包实测**（见第 5 节坑 7 的事故） |
| 拖拽的固实包优化 | 多文件拖出目前每个流独立解码（O(n²)）；见 `NOTES-drag-out.md` 第八节 |
| 拖拽还原空目录 / 提供 MOVE 语义 | 描述符只列文件；只给 `DROPEFFECT_COPY` |
| 同名冲突交互选项 | 固定"就地替换"（与 7-Zip `a` 一致） |

**已交付、不用再做**：RAR 分卷（含 `.part01.exe`）、
压缩格式矩阵、追加/更新、拖拽添加、4 套主题、Win11 现代右键菜单。

---

## 4. 下一步建议（按优先级）

1. 右键菜单加「添加到压缩包」（复用 `ArchiveUpdateEngine`，只差注册表动词 + 重注册）
2. 「解压后删除分卷」的**干跑预览**：解压前告诉用户「预计回收 X GB」，比一个复选框更让人放心
3. RAR 分卷的中途回收（**先写大包实测再动手**，别重演坑 7 的事故）
4. 拖拽的固实包单会话多流优化（注意 shell 乱序读会死锁，见 `NOTES-drag-out.md` 第八节）
5. 拖拽还原空目录 + `DROPEFFECT_MOVE` 语义

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

### 坑 5：分卷有**两种布局**，喂错了就是"数据损坏"

| 布局 | 例子 | 怎么喂 7z.dll |
|---|---|---|
| **切片卷** | `.7z.001`、`.zip.001`、`.z01…zip` | **拼成一条连续流**（现有做法） |
| **独立卷** | `.part1.rar`、`.r00`、**`.part01.exe`** | **只给第一卷** + 实现 `IArchiveOpenVolumeCallback` 让 handler 按名字取其余的 |

RAR 是独立卷：把它的卷拼起来 → `解压 0 项 · 失败 1 项 · 原因：数据损坏`（实测）。
`GetProperty(kpidName)` 必须回**带扩展名的文件名** —— 7-Zip 的 `CVolumeName::InitName`
会自己把 `.exe` 映射成 `.rar`，所以 **`.part01.exe` 首卷原生就支持，不用特殊代码**。
删除判据要给卷尾结构记录留余量（实测差 76 字节），逐字节全覆盖会让 RAR 卷永远删不掉。
完整版见 `NOTES-7z-com-interop.md` 第六节。

### 坑 6（本轮新增）：右键菜单的**可见性判定**是两份，漏一处就等于功能不存在

`Build.25778117.part01.exe`（RAR 自解压首卷）在**两条菜单里都看不到解压动词** ——
经典菜单靠 `AppliesTo` 的扩展名名单（`.exe` 不在里面），Win11 现代菜单靠
`SliceArc.ShellExt` 里的 `ArchiveGuessing`（同样不含 `.exe`）。功能没坏，是**入口被藏了**。

- 判定逻辑已抽成共享源码 `src/Shared/ArchiveNameHeuristics.cs`，`Core` 与 `ShellExt`
  **各自链接编译同一份**（ShellExt 不能引用 Core：会把 7z.dll 拖进 explorer 的 COM 代理）。
- AQS 那一半没法共享，记住结论：**`~`（包含）在 `AppliesTo` 里不生效，通配符 `*` 生效**：
  正确写法 `System.FileName:*.part*.exe`；`~".part"` 会让动词**直接消失**。
- 禁止直接列 `.exe`：解压动词会挂到全盘每个可执行文件上。
- 覆盖：Probe **8.4**（对真实菜单）+ **第 12 节**（对共享判定逻辑）。

---

### 坑 7（本轮新增）：**独立卷（RAR）不能边解边删** —— 会删出"数据损坏"

17 卷 ×4GB 的 Rar5 包，勾「解压后删除已用完的分卷」→ 提示"已损坏、不完整"、
但文件基本都在、且好几个分卷没删干净。链路：解码器持有每卷一个流并会**回头读** →
被删的卷读失败 → `hr != S_OK` → `RecordFailure("解压中断", "…不完整…")` → `Ok=false` 弹警告；
中断又让后面的卷读不完，于是永远删不掉。实测复现：解压中途手动删掉还没轮到的卷，
必得 `解压 3 项 · 失败 1 项 · 原因：数据损坏`。

已改为：**独立卷只在整包成功结束后统一回收**（全量成功时按"整个集合已消费"判定，
因为解码器读不到卷尾 ~76 字节结构记录、纯终止卷完全不读）；单卷删除失败只记 warning，
不再把整次解压判成失败；`Extract` 抛异常也会跑一次最终回收。
**切片卷（.7z.001/.z01）不受影响，仍然是真·边解边删。**
细节与复现脚本见 `NOTES-7z-com-interop.md` 第六节；回归断言 Probe 11.5 / 11.6。

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

出测试用构建（**dist 里的安装包是快照，别拿它当被测对象**）：

```powershell
dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -o D:\SliceArc\artifacts\bin
$App = 'D:\SliceArc\artifacts\bin\SliceArc.App.exe'
```

按改动范围加跑：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\cli-e2e-test.ps1     -App $App   # 解压 + 分卷边解边删（切片卷）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\rar-sweep-e2e-test.ps1 -App $App # RAR 分卷回收：中途不许删 / 成功后清干净
powershell -NoProfile -ExecutionPolicy Bypass -File tools\add-e2e-test.ps1     -App $App   # 追加/更新 + 不支持格式安全拒绝
powershell -NoProfile -ExecutionPolicy Bypass -File tools\compress-formats-test.ps1 -App $App   # 压缩格式矩阵（含 zst 拒绝）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drag-add-e2e-test.ps1            # 真实 OLE 拖拽投递
powershell -NoProfile -ExecutionPolicy Bypass -File tools\theme-smoke-test.ps1  -App $App   # 4 套主题
powershell -NoProfile -ExecutionPolicy Bypass -File tools\inspect-ui.ps1        -App $App -Target <压缩包>
```

Probe 分节（改动哪块就跑哪节，但整跑也就十几秒）：

| 节 | 覆盖 |
|---|---|
| 1–7 | 7z.dll 句柄 / 分卷识别 / 切片卷解压 / 路径穿越 / CCW / 引擎 |
| 8 | **真实 shell 菜单可见性**（8.4 = `.part01.exe` 上必须出现解压动词）+ 右键注册表读回 |
| 9 | 拖拽直出协议（零临时文件） |
| 10 | `ArchiveVerifier` 批量校验 |
| 11 | **RAR 分卷**：命名规则 / `.part01.exe` / 边解边删 → **11.5 中途删除必须为 0**、**11.6 切片卷必须仍中途删除** |
| 12 | 右键菜单可见性判定（`src/Shared/ArchiveNameHeuristics.cs`，两个菜单共用同一份源码） |

Probe 会自动用 `7z.exe a -t7z -mx1 -v1m` 造测试包（不可压缩随机数据），工作目录
**`D:\SliceArc\artifacts\probe-work`**（每次重跑自动清空；`SLICEARC_PROBE_WORK` 可覆盖）。
第 11 节需要 `C:\Program Files\WinRAR\Rar.exe`（RAR 无法用 7z.exe 创建），没有就自动跳过。

> ⚠️ **别把工作目录改回 `%TEMP%`**：那在 C 盘，而本机 C 盘只剩几个 GB、owner 的原则是
> "所有产物落 D 盘"。测试脚本也都把默认 `-Root` 放在 `D:\SliceArc\artifacts\` 下 ——
> 新增脚本请照抄这个默认值。唯一还在 C 盘的是 `tools\package.ps1` 里 MSIX 打包用的
> `%TEMP%\SliceArcMsix` 暂存（几十 KB，脚本自己会删）。

**加新功能时，先往 Probe 里加断言再改 Core**——这套验证是唯一能防住 COM 层回归的东西。
（`dotnet build` 的 `[exit code: 1]` 常见于 PowerShell 把 stderr 上的诊断行当错误，
看输出里有没有「全部通过」才算数；诊断行现在默认关闭，`VolumeAwareStream.Diagnostics = true` 才打印。）

### 写测试脚本时的三个硬规矩（都是踩出来的）

1. **WinExe 必须 `Start-Process -Wait -PassThru`**：PowerShell 用 `&` 起 GUI 子系统程序**不会等**，
   `$LASTEXITCODE` 是上一条命令的，断言全部变成假失败。
2. **含中文的 `.ps1` 必须补 UTF-8 BOM**：PS 5.1 按 GBK 读无 BOM 文件 → 中文字符串炸语法。
   ```powershell
   $p='tools\x.ps1'; $t=[IO.File]::ReadAllText($p,(New-Object Text.UTF8Encoding($false)))
   [IO.File]::WriteAllText($p,$t,(New-Object Text.UTF8Encoding($true)))
   ```
3. **别让局部变量覆盖参数**：PowerShell 变量名**大小写不敏感**，`$probe = ...` 会覆盖参数 `$Probe`，
   第二次调用就变成"执行一个对象"。

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
- ❌ 不要在 update 模式下**先截断原归档**：必须写临时文件、成功后再 `File.Move` 替换
- ❌ 不要为不支持更新的格式做任何写入尝试 —— 打开输出流之前就要拒绝（`NOTES-add-to-archive.md` 第三节）
- ❌ 不要把 `ArchiveUpdateEngine` 里的 `kUpdate` 判断换成写死的格式名单 —— 那是从 7z.dll 查出来的
- ❌ 不要在测试里从**别的进程**对窗口 drop target 调 `GetObjectForIUnknown` —— 那是别人的地址空间，CLR 直接崩
- ❌ 不要把 RAR 分卷（独立卷）拼成一条流 —— 那是给 .7z.001 切片卷用的，RAR 会报数据损坏
- ❌ 不要在 IArchiveOpenVolumeCallback.GetProperty(kpidName) 里回全路径或去掉扩展名的基名 —— 卷名推导靠它
- ❌ 不要删 .refs/src（GUID、接口签名、update 契约、卷名推导规则的唯一权威来源）
