# SliceArc — 向现有压缩包添加文件（update 模式）

> 2026-10-13。计划书在 `.trae/documents/添加到现有压缩包.md`，本文记录**实现后的实测结论**与
> 自动化验证方式（含"怎么在无人拖鼠标的情况下真的测一次拖拽"）。
> 环境：Windows 11 / .NET 8 / 7-Zip 26.04 / x64。

---

## 一、能力边界（实测确认，不是推测）

| 格式 | 能解压 | 能添加/更新 | 说明 |
|---|---|---|---|
| `.7z` | ✅ | ✅ | 主力格式 |
| `.zip` | ✅ | ✅ | |
| `.tar` | ✅ | ✅ | |
| `.wim` | ✅ | ✅ | handler 声明 `kUpdate` |
| `.rar` | ✅ | ❌ | 只读 |
| `.gz` `.bz2` `.xz` `.zst` | ✅ | ❌ | 单流格式，没有"多条目"概念 |
| `.lz` `.Z` `.lzh` `.arj` `.cpio` | ✅ | ❌ | handler 不支持更新 |
| `.lz4` `.br` | ❌ | ❌ | 随包 7z.dll **没有这两个 handler**，既不能加也不能解压 |

判定依据：`SevenZipLibrary.SupportsWrite(format)` 读的是 `kUpdate`（`GetHandlerProperty2`），
不是写死的格式名单。**换 7z.dll 版本时这个能力表会自动跟着变。**

---

## 二、7-Zip 的 update 契约（读 `.refs` 源码 + 实测确认）

1. **必须同一个 handler 实例**：先 `IInArchive::Open` 旧归档，再从这个对象 QI 出 `IOutArchive`
   调 `UpdateItems`。权威依据 `CPP/7zip/UI/Common/Update.cpp` L364-376（`QueryInterface(IID_IOutArchive)`）。
   handler 自己负责把"保留条目"的旧数据从它打开的旧归档里复制出来并重写整个新归档。
2. **回调只为新数据供数据**：`GetUpdateItemInfo` 的 `indexInArchive=-1` 表示新条目、`>=0` 表示复用旧归档第 N 项；
   `GetStream` 只在 `newData=1` 时被调用；`!newData` 时返回 `E_FAIL` 是标准做法。
3. **`GetProperty` 只在 `newProps=1` 时被调用**（`7zHandlerOut.cpp` L488、`ZipHandlerOut.cpp` L146）——
   保留条目的名称/大小/属性由 handler 从旧归档自取，回调不用转发。这条极大简化了实现。
4. **输出必须是全新文件**：handler 从 offset 0 开始重写，所以先把新归档写进同级临时文件，
   成功后再 `File.Move` 替换原文件。

---

## 三、安全模型（不允许任何破坏性写入）

- 不支持更新 / 分卷 / 无法识别 → 抛 `UnsupportedArchiveUpdateException`（带中文原因 + 建议），
  **在打开输出流之前就返回**，原文件连时间戳都不动。
- 更新过程写 `*.tmp-<guid>`，任一步失败 → 删临时文件、原归档原样保留。
- **分卷压缩包明确不支持**：`VolumeDetector.DetectGroup` 判定为多卷就直接拒绝
  （重建整组卷风险高、收益低，留作后续迭代）。

自动化断言（见第五节）里专门有一条：**每种不支持格式跑完后，原文件哈希与 mtime 必须完全不变，
且目录里不能留下任何 `.tmp-*` 文件。**

---

## 四、三个入口

| 入口 | 位置 | 行为 |
|---|---|---|
| 工具栏「添加」 | 打开压缩包后可用 | 多选文件对话框 → 追加 |
| **拖入窗口** | 已打开压缩包时，把文件/文件夹拖进主窗口 | 视为"添加到当前压缩包"；**没有打开压缩包时保持原语义**（拖入压缩包 = 打开它） |
| CLI | `--add-to <归档> --add <源>` | 追加/更新，供脚本与右键动词复用 |

同名条目按 7-Zip `a` 的惯例**就地替换**，不新增重命名/跳过选项。

---

## 五、验证方式（三条自动化，全部实测通过）

```powershell
cd D:\SliceArc
$App = 'D:\SliceArc\artifacts\bin\SliceArc.App.exe'    # dotnet publish 出来的测试用包

# 1) CLI 追加：7z/zip/tar 的 追加+保留+替换，不支持格式与分卷的安全拒绝
powershell -NoProfile -ExecutionPolicy Bypass -File tools\add-e2e-test.ps1 -App $App

# 2) 真实 OLE 拖拽投递（不是模拟 WPF 事件，见第六节）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drag-add-e2e-test.ps1

# 3) 主题 + UI 渲染
powershell -NoProfile -ExecutionPolicy Bypass -File tools\theme-smoke-test.ps1
```

**验证口径**：归档内容一律用 **7z.exe 独立核对**（`7z l -slt` 列表 + `7z x` 解出来比 SHA256），
不用被测代码自己验自己。

`tools\add-e2e-test.ps1` 覆盖：
- 7z / zip / tar：**追加分支**（新条目进包、原条目字节不变）、**保留分支**、**同名替换分支**（条目数不增、内容是新的）
- 9 种不支持格式：退出码非 0 + **原文件哈希与 mtime 不变** + 无临时文件残留
- 分卷组：拒绝且所有卷哈希不变

`tools\drag-add-e2e-test.ps1` 覆盖：拖单个文件、拖整个目录（含嵌套文件）、同名文件再拖一次（替换不重复）。

---

## 六、怎么在没有人拖鼠标的情况下真的测一次拖拽

这是本轮最花时间、也最值得记下来的部分。结论：**必须让投递发生在应用进程内**，
并且要绕开三个"进程内 COM 身份表"陷阱。

### 6.1 为什么不能从外部进程投递

`RegisterDragDrop` 把 drop target 存成窗口属性 `OleDropTargetInterface`。
那是**目标进程地址空间里的指针**；在别的进程里对它调 `Marshal.GetObjectForIUnknown`
会去读别人的内存 → `Fatal error. Internal CLR error. (0x80131506)`。
真正的跨进程拖拽只有 OLE 自己的封送（在模态 `DoDragDrop` 循环里）才做得到，
而那需要真实鼠标输入。

所以 `tests/SliceArc.DropProbe` 的做法是：**引用 `SliceArc.App` 工程，自己构造真实的
`MainWindow`，在 UI 线程上对自己的窗口做 `DragEnter`/`DragOver`/`Drop`**。

### 6.2 三个必须绕开的陷阱

1. **进程内拿不到 RCW**。WPF 的 drop target 是托管对象 `OleDropTarget`，
   `GetObjectForIUnknown` 会直接还你那个托管实例，转 `ComImport IDropTarget` 必然
   `InvalidCastException`。→ **直接读 vtable 槽位调用**（IDropTarget：3=DragEnter、4=DragOver、6=Drop）。
   （与 `NOTES-drag-out.md` 坑 1 是同一个身份表现象的两个方向。）

2. **数据对象必须同时实现 BCL 的 `ComTypes.IDataObject`**。进程内 WPF 是拿托管实例做类型检查的，
   只实现自己那套 `IOleDataObject` 会得到 `E_NOINTERFACE`。
   跨进程时这一步由 RCW 天然满足，所以**这是纯测试宿主的需求**。

3. **`EnumFormatEtc` 不能返回 null**。WPF 用它建格式列表；返回 null 的话
   `QueryGetData` 仍然成功（所以 `DragOver` 会显示"可放下"），但 **`GetData` 永远不被调用** →
   处理函数里 `e.Data.GetData(FileDrop)` 拿到 null → 静默什么都不做。
   这个坑的迷惑性极强：界面表现完全正常，就是没反应。

### 6.3 另外两个宿主细节

4. **别实例化 `SliceArc.App.App`**：它的 `OnStartup` 会按命令行再造一个 `MainWindow`，
   于是有两个同标题窗口抢同一次投递。用普通 `System.Windows.Application`
   并把 `Themes/Minimal.xaml` 手动并进 `Resources` 即可（`App.CurrentTheme` 是静态属性，默认 Light）。
   另外 `MainWindow.xaml` 的 `Icon="/Assets/SliceArc.ico"` 是 pack URI，**按入口程序集解析**，
   所以探针工程要自己带一份该图标资源（`<Resource Include=... Link="Assets\SliceArc.ico" />`）。

5. **必须自己装 `DispatcherSynchronizationContext`**。探针不走 `Application.Run()`，
   不装这个的话 `await Task.Run(...)` 的后续会落到线程池线程上，碰 UI 对象抛跨线程异常，
   被 `_ = AddToAsync(...)` 静默吞掉——**表现是"归档确实被改了，但界面不刷新、日志也没写"**。
   装上之后行为与真实运行一致。

### 6.4 WPF 拖放语义（实测，别照直觉写）

- `DragEnter` 返回 `S_FALSE` 且 effect=0 是**正常的**：应用只处理 `DragOver`（常见写法），
  真正的放行判定发生在 `DragOver`。探针必须像真实鼠标那样连续发几次 `DragOver` 再 `Drop`。
- **`Drop` 返回 `S_FALSE` 是成功**，不是失败。
- `Drop` 之后 effect 变成 0 也正常（Drop 事件的 `e.Effects` 不参与）。

---

## 七、还没做（明确不在此次范围）

- 资源管理器右键"添加到压缩包"动词（要改注册表 + 重注册装机脚本）。
- 分卷压缩包的追加与"追加时输出分卷"。
- 同名冲突的"重命名保留 / 跳过"交互选项（当前固定为就地替换）。
- 添加时的进度条目前复用压缩进度面板，粒度是"条目级"。
