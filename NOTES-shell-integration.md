# SliceArc — 原生右键菜单 / 命令行契约踩坑记录

> 2026-10-12 实测。所有结论都有对应的自动验证：`tests/SliceArc.Probe` 第 7、8 节，
> 以及 `tools/cli-e2e-test.ps1`、`tools/inspect-ui.ps1`。
> 环境：Windows 11 / .NET 8.0.425 / 7z.dll 26.04 / HKCU 免管理员。

---

## 一、为什么是「注册表静态动词」而不是 Shell Extension DLL

需求是「原生右键能快速选解压」。可选两条路：

| 方案 | 代价 | 结论 |
|---|---|---|
| `IContextMenu` Shell Extension DLL | 需要 COM 注册、进程内加载进 explorer.exe、32/64 位各一份、崩溃会带崩资源管理器、卸载要注销 COM | ❌ 对这个需求过重 |
| HKCU 静态动词 + 级联子菜单 | 纯注册表，免管理员，删键即卸载 | ✅ 采用 |

静态动词的天然短板是「不能动态算输出目录」，所以把复杂度放到**被调用进程的命令行**里：
菜单项只负责把 `%1` 和模式开关传给 `SliceArc.App.exe`，真正的分卷识别 / 命名 / 删除策略
全在 Core 里（和 GUI 走同一套代码，不存在两套逻辑）。

---

## 二、注册表结构（唯一权威形状：**平铺**，不要级联）

全部在 `HKEY_CURRENT_USER` 下，**永远不需要管理员**：

```
HKCU\Software\Classes
├── *\shell\                              ← 任意文件（所有动词平铺在这里）
│     SliceArc01_here        MUIVerb="SliceArc：解压到当前文件夹"       [AppliesTo]
│         command = "…\SliceArc.App.exe" --extract "%1" --here --quiet
│     SliceArc02_sub         MUIVerb="SliceArc：解压到「归档名」文件夹" [AppliesTo]
│         command = "…\SliceArc.App.exe" --extract "%1" --to-subfolder --quiet
│     SliceArc03_here_delete MUIVerb="SliceArc：解压并删除已用完的分卷" [AppliesTo]
│         command = "…\SliceArc.App.exe" --extract "%1" --here --delete-spent --quiet
│     SliceArc04_test        MUIVerb="SliceArc：测试压缩包完整性"       [AppliesTo]
│         command = "…\SliceArc.App.exe" --extract "%1" --test --quiet
│     SliceArc10_open        MUIVerb="用 SliceArc 打开"                 [AppliesTo]
│         command = "…\SliceArc.App.exe" "%1"
├── Directory\shell\                      ← 右键文件夹（%1 = 该文件夹）
│     SliceArc01_all  → --extract-all "%1" --here --quiet
│     SliceArc10_open → "%1"
└── Directory\Background\shell\           ← 文件夹空白处（%V = 当前目录）
      SliceArc01_all  → --extract-all "%V" --here --quiet
      SliceArc10_open → "%V"
```

动词名统一带 `SliceArc` 前缀，`Unregister` 只删自己前缀的键 ——
**绝不能删 `shell` 本身**，那是所有程序共用的。

### 关键点 1：为什么是平铺，不是级联 —— 这是踩出来的

级联看着更清爽，但**在 HKCU（免管理员）范围内，本机试过的三种写法全部失败**：

| 写法 | 结果 |
|---|---|
| `SubCommands = ""` + 嵌套 `shell\` 子键 | **空壳**：子菜单展开是空的，父项没有 `command`，点了没反应 |
| `ExtendedSubCommandsKey` 指向 `*\ContextMenus\SliceArc` | **仍然空** |
| `ExtendedSubCommandsKey` 指向顶层 `SliceArcMenu\Files` | **仍然空** |

原因（文档说的和直觉不一样）：

- `SubCommands` **不是**「去读嵌套的 shell 子键」，它的值是**要显示的动词列表**。空串 = 空列表。
  [MS Learn: SubCommands](https://learn.microsoft.com/en-us/windows/win32/shell/how-to--create-cascading-menus-with-the-subcommands-registry-entry)
- `ExtendedSubCommandsKey` 的值是按 HKEY_CLASSES_ROOT 解析的键路径，文档说可以放 HKCU，
  但本机实测三种位置都解析不出子项。
  [MS Learn: ExtendedSubCommandsKey](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-create-cascading-menus-with-the-extendedsubcommandskey-registry-entry)
- **7-Zip 不是反例**：它的子菜单是 `IContextMenu` shell extension（HKLM 里根本没有它的静态动词键），
  没有可以照抄的注册表形状 —— 别浪费时间去翻 7-Zip 的注册表。

结论：平铺。UI 上用「SliceArc：」前缀把它们视觉上归为一组，少点一层还更快。

### 关键点 1b：级联子项读不回来 = 当初没被发现的原因

`IContextMenu::QueryContextMenu` 只建子菜单句柄，子项由 shell 在用户展开时惰性创建，
所以**级联即使坏了，探测出来也是"有子菜单句柄但没内容"，和正常的没法区分**。
现在平铺了，直接断言「4 个命令都在顶层菜单里」就够了（Probe 8.2）。

### 关键点 2：`AppliesTo` 在 `*\shell` 上确实生效（实测）

```
Probe 8.1  压缩包 (sample.7z.001) 上能看到 SliceArc 命令组 = True
Probe 8.x  普通文件 (not-a-archive.bin) 上出现              = False   ← AppliesTo 生效
```

所以不需要给每个扩展名都写一遍 `SystemFileAssociations\.7z\shell\...`。
`AppliesTo` 是 AQS 表达式：`System.FileExtension:=".7z" OR System.FileExtension:=".zip" OR ...`。
注意 `.7z.001` 的 `System.FileExtension` 是 **`.001`**，所以分卷后缀要单独列（`.001` `.z01` `.r00` `.r01` `.part1`）。

想让菜单出现在所有文件上：`SliceArc.App.exe --register --all-files`（注册时不写 `AppliesTo`）。

### 关键点 2b：AQS 里能用什么、不能用什么（2026-10-13 二分实测）

起因：用户右键 `Build.25778117.part01.exe`（RAR 自解压**首卷**）时，**四个解压动词一个都不显示**，
连「解压并删除已用完的分卷」也没有 —— 而它的 `System.FileExtension` 是 `.exe`，不在扩展名名单里。
功能本身是好的（命令行 `--delete-spent` 实测 13 卷回收 12 卷），纯粹是**菜单没给入口**。

把 `AppliesTo` 换成候选条件逐个跑 Probe 8.4，结论（**照抄，别猜**）：

| 候选 `AppliesTo` | 结果 |
|---|---|
| `System.FileExtension:=".exe"` | ✅ 生效（对照组） |
| `System.FileName:="Build.25778117.part01.exe"` | ✅ 生效（属性名对，`=` 可用） |
| `System.FileName:~".part"` | ❌ **不生效** —— 动词直接消失 |
| **`System.FileName:*.part*.exe`** | ✅ **生效（通配符可用）** |

**结论：`AppliesTo` 的 AQS 里 `~`（包含）不工作，通配符 `*` 工作。**
最终写法：

```
... OR System.FileName:*.part*.exe
```

注意**不能**直接列 `.exe`：那会把解压动词挂到全盘每一个可执行文件上。

另一处同源逻辑在 Win11 现代菜单里（`SliceArc.ShellExt` 的 `GetState`），两边曾经各写一份、
于是**同时漏掉** `.part01.exe`。现在判定逻辑抽成共享源码
`src/Shared/ArchiveNameHeuristics.cs`，由 `SliceArc.Core` 和 `SliceArc.ShellExt` **各自链接编译同一份**
（ShellExt 不能引用 Core —— 那会把 7z.dll 拖进 explorer 的 COM 代理进程）。
AQS 那一半没法共享代码，靠 **Probe 8.4** 对着真实菜单兜底。

`AppliesTo` 里 `.exe` 要慎用的另一个原因：`System.FileExtension` 取的是**最后一段扩展名**，
所以 `a.part01.7z.001` 是 `.001`、`a.rar` 是 `.rar`，而 `x.part01.exe` 就是 `.exe` —— 分卷后缀
必须一个个列，模式类的一律靠 `System.FileName` 通配符。

### 关键点 3：Windows 11 的新右键菜单

Win11 把第三方静态动词收进「显示更多选项」（Shift+F10 / 旧版菜单）。这是系统行为，不是注册错误。

### 关键点 4：注册/注销后要通知 shell

`SHChangeNotify(SHCNE_ASSOCCHANGED, …)` —— explorer 会缓存关联数据，不通知的话新菜单可能要等
下次登录或重启 explorer 才出现。注销同理。

---

## 三、命令行契约（= 菜单里写死的字符串）

```
SliceArc.App.exe [<压缩包或文件夹>]                打开主窗口
SliceArc.App.exe --extract <压缩包> [选项]         直接解压（弹紧凑进度窗，完成自动关闭）
SliceArc.App.exe --extract-all <文件夹> [选项]     解压文件夹内全部压缩包
SliceArc.App.exe --register [--all-files]          安装右键菜单
SliceArc.App.exe --unregister                      卸载右键菜单
SliceArc.App.exe --shell-status                    打印注册状态与全部动词
SliceArc.App.exe --help                            用法
```

选项：`--here`（默认，解压到归档所在目录）、`--to-subfolder`、`--out <目录>`、
`--delete-spent`、`--keep-last <n>`（默认 1）、`--no-overwrite`、`--test`、
`--quiet`、`--password <口令>`、`--path <归档内路径>`（可重复，精确选择）。

**改这些 flag 名字 = 必须重新 `--register`**，因为注册表里存的是完整命令行字符串。

---

## 四、本轮踩到的坑（都不在 7-Zip 那一侧）

### 坑 1：`IShellFolder::GetUIObjectOf` 的 `PCUITEMID_CHILD_ARRAY` 必须标 `LPArray`

```csharp
// ✗ AccessViolationException：默认 marshaler 交出去的是 SAFEARRAY，shell 顺着它走到野内存
int GetUIObjectOf(IntPtr hwnd, uint cidl, IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);

// ✓
int GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
                  ref Guid riid, IntPtr reserved, out IntPtr ppv);
```
同一个坑 `IInArchive::Extract(uint[] indices)` 也踩过（那边早就标了 LPArray）。
**规则：COM 接口里出现数组参数，一律显式标 `LPArray`。**

### 坑 2：`CoTaskMemFree` 在 `ole32.dll`，不在 `shell32.dll`

`EntryPointNotFoundException: Unable to find an entry point named 'CoTaskMemFree' in DLL 'shell32.dll'`。
（`SHParseDisplayName`/`SHBindToParent` 在 shell32，释放 PIDL 在 ole32。）

### 坑 3：PowerShell 用 `&` 起 WinExe **不会等**

`& .\SliceArc.App.exe --extract ...` 立刻返回，`$LASTEXITCODE` 是上一条命令的，
断言全部在解压完成前跑 → 全是假失败。必须：

```powershell
$p = Start-Process -FilePath $exe -ArgumentList $args -Wait -PassThru
$exit = $p.ExitCode
```

### 坑 4：子目录里的 XAML 写相对 Icon 路径会 `XamlParseException`

```
Icon="Assets/SliceArc.ico"      ✗  Views/ 下的页面按 Views/ 解析 → Views/Assets/... 不存在
Icon="/Assets/SliceArc.ico"     ✓  前导斜杠 = 应用根
```
症状：`在"System.Windows.Baml2006.TypeConverterMarkupExtension"上提供值时引发了异常`
（没有更具体的提示，很难猜）。另外：WinExe 的 `DispatcherUnhandledException` 里弹 MessageBox，
在被脚本/资源管理器拉起时会**挂住不返回**，所以 `--quiet` 模式下只记日志、不弹窗。

### 坑 5：WinExe 的 stdout 编码

`new StreamWriter(Console.OpenStandardOutput())` 默认 UTF-8，但 `AttachConsole` 接上的控制台
是 936 → 中文全乱码。修法：`GetConsoleOutputCP()` 拿当前代码页，用它建 StreamWriter
（**不要**去改 `Console.OutputEncoding`，那会连带改掉父进程共享的控制台代码页）。
日志文件永远用 UTF-8，与这条无关。

### 坑 6：`Window.Icon` / `ApplicationIcon` 之外还要 `<Resource Include>`

`<ApplicationIcon>` 只影响 exe 的资源节；`Window.Icon="/Assets/x.ico"` 走的是 pack URI，
文件必须同时以 `<Resource>` 打进程序集，否则运行时抛异常。

### 坑 7：`--unregister` 和 `--register` 连着跑会互相踩（坑 3 的延伸）

我自己的验证脚本里写过：

```powershell
& $exe --unregister      # 不等！
& $exe --register        # 两个进程并发跑
```

结果日志是「已注册 → 已移除」，新注册被后来的注销删掉，注册表变空。
**只要连续调用多个 WinExe，每一个都必须 `Start-Process -Wait`。**

### 坑 8：`Unregister` 不能删 `shell` 键

`*\shell`、`Directory\shell` 是所有程序共用的键。注册器只删带 `SliceArc` 前缀的动词子键；
删父键会把别的软件的菜单项一起干掉。

---

## 五、验证命令（改任何相关代码后都要跑）

```powershell
# 1) Core + 引擎 + 注册表 + shell 可见性 + 拖拽协议
cd D:\SliceArc
dotnet run --project tests\SliceArc.Probe\SliceArc.Probe.csproj -c Release
#    → 期望 "===== 全部通过 ====="（第 8 节需要先 --register，否则自动跳过）

# 2) 发布 + 注册（注意：每个 WinExe 调用都要等！）
dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -o D:\SliceArc\dist
$exe = 'D:\SliceArc\dist\SliceArc.App.exe'
$p = Start-Process -FilePath $exe -ArgumentList '--unregister' -Wait -PassThru
$p = Start-Process -FilePath $exe -ArgumentList '--register'   -Wait -PassThru
Start-Process -FilePath $exe -ArgumentList '--shell-status' -Wait

# 3) 完全按右键菜单的命令行跑一遍（造 6 卷包 → 解压 → 校验 → 删卷 → 批量 → 测试）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\cli-e2e-test.ps1

# 4) UI 真的画出来了吗（UI Automation 遍历控件树）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\inspect-ui.ps1 -Target <某个分卷.001>

# 5) 卸载
Start-Process -FilePath $exe -ArgumentList '--unregister' -Wait
```

---

## 六、当前实测状态（2026-10-12 改平铺后）

- Probe 第 1–9 节：**全部通过**
  - 第 7 节：平铺动词齐全、每个都自带 `command`、注销干净
  - 第 8 节：shell API 读回真实菜单，**4 个解压命令都直接出现在顶层**；
    `AppliesTo` 生效（普通文件上不出现）
- `tools/cli-e2e-test.ps1`：**全部通过** —— 6 卷包 → `--to-subfolder --delete-spent` →
  产物 SHA256 一致、5 卷被回收、只留最后一卷（保险丝）
- `tools/inspect-ui.ps1`：64 个控件，工具栏/复选框/地址栏/6 列表头/数据行/状态栏全部就位
- 本机已注册：`HKCU\Software\Classes\*\shell\SliceArc*` → `D:\SliceArc\dist\SliceArc.App.exe`
