# 超速解压缩（SliceArc）

Windows 上的**分卷压缩包解压器**。格式解析全部交给 7-Zip 官方的 `7z.dll`，本项目不自己实现任何格式解析器。

## 核心卖点

1. **边解压边回收分卷** —— 解压多卷压缩包时，已经读完的分卷立即删除。解压一个 50 卷的包不会同时占用两份磁盘空间。可选开关，并带「保留最后 N 卷」保险丝；分卷不完整时自动禁用删除。
2. **拖拽直出（零临时文件）** —— 把文件/目录从窗口直接拖到资源管理器，边解码边写入最终路径，全程不落地临时文件。
3. **Bandizip 风格的极简界面** —— 一行工具栏 + 一行地址栏 + 一个列表 + 一行状态栏，没有多步向导。解压时显示**双进度条**（上方总进度、下方当前文件进度）。

## 功能一览

### 解压
- 支持 `7z.dll` 内置的全部格式处理器：7z / zip / rar / tar / gz / bz2 / xz / zst / cab / iso / wim 等
- 分卷识别：`.7z.001`、`.part1.rar`、`.rar` + `.r00`、`.z01…zip`
- 加密压缩包：自动识别并弹出密码框，支持重试；错误口令与数据损坏可区分
- 路径穿越防护、zip 炸弹上限、取消
- 失败时给出「原因 + 可操作建议」，并只报告首个失败原因（避免级联错误淹没真实问题）
- 所有用户可见错误均为中文，不暴露原始错误码

### 压缩
- 输出格式：7z / zip / tar / gz / bz2 / xz / zst / wim
- 压缩级别 0–9（存储 ~ 极限），可指定分卷大小

### 其他
- **工具菜单**：压缩包完整性校验、批量重命名
- **4 套主题**：浅色 / 深色 / 柔和 / Bandizip 风格；深色主题下原生标题栏同步适配
- **文件关联 + 资源管理器右键菜单**（免管理员）：解压到当前文件夹 / 解压到「归档名」文件夹 / 解压并删除已用完的分卷 / 测试压缩包完整性 / 用本程序打开
  - Windows 11 现代一级菜单由稀疏 MSIX + COM ShellExt 提供，注册前会判断系统版本（Win10 走经典注册表菜单，跳过 UAC 提示）
- **命令行模式**，供脚本与右键动词调用（`--extract` / `--extract-all` / `--test` / `--register` / `--unregister` / `--shell-status`）
- 便携单文件 EXE（.NET 运行时与 `7z.dll` 全部内嵌），设置与日志写在 exe 同级目录，不写 `%APPDATA%`

## 项目结构

```
src/SliceArc.Core/     引擎：分卷探测、7z.dll COM 互操作、解压/压缩/校验、拖拽直出
src/SliceArc.App/      WPF 界面、命令行入口、主题、右键菜单注册
src/SliceArc.ShellExt/ Windows 11 现代右键菜单的 COM shell 扩展（稀疏 MSIX）
tests/SliceArc.Probe/  端到端自检（造包 → 解压 → SHA256 → 删卷 → 右键菜单回读 → 拖拽协议）
tools/                 打包、图标生成、CLI 端到端测试、UI 自动化检查
docs/index.html        项目落地页
```

## 构建

**要求**：Windows x64、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
# 编译（必须 0 错误 0 警告）
dotnet build SliceArc.sln -c Release

# 跑端到端自检，期望输出「全部通过」
dotnet run --project tests\SliceArc.Probe\SliceArc.Probe.csproj -c Release

# 便携单文件 EXE → dist\portable\SliceArc.App.exe
dotnet publish src\SliceArc.App\SliceArc.App.csproj -c Release -p:PublishProfile=Portable

# 一键打包（便携版；加 -Installer 额外产出 Inno Setup 安装包）
.\tools\package.ps1 -Installer
```

> `-Installer` 与 Windows 11 现代右键菜单需要额外环境：Inno Setup 6、Windows SDK（`makeappx.exe` / `signtool.exe`），以及一张 `CN=SliceArc` 的代码签名证书。只做便携版时不需要这些。

## 实现说明

几个关键设计点，细节见仓库内的 `HANDOFF.md` 与其引用的 NOTES：

- **COM 接口扁平声明**：7z.dll 的接口在 C# 侧必须把所有槽位平铺写在同一个接口里，不能靠接口继承——否则 vtable 错位会导致 CLR 致命错误（`0x80131506`）。
- **分卷合并流**：`VolumeAwareStream` 把多个物理分卷伪装成一个连续流，并逐字节记录覆盖情况；「某分卷已消费完」的判据是 `[0, 卷末尾)` 被**单个连续区间**覆盖。
- **拖拽直写**：自实现 COM `IDataObject`，按 lindex 产出 `IStream`，后台解码经有界队列（背压）直接写入 shell 现场创建的目标文件。

## 第三方组件

- `7z.dll` 来自 [7-Zip](https://www.7-zip.org/)，遵循其许可（LGPL，含 unRAR 限制）。

## 已知限制

- `--extract-all` 批量解压暂不支持对整批加密包输入统一口令（会逐个提示/跳过）。
- 归档内右键「解压选中项到…」的默认目录目前固定为「上次用过的目录」。
- 拖拽多文件时不还原空目录，只提供复制语义。
