# 极限解压（SliceArc）

Windows 上的**分卷压缩包解压器**。格式解析全部交给 7-Zip 官方的 `7z.dll`，本项目不自己实现任何格式解析器。

## 核心卖点

1. **回收分卷** —— **切片分卷**（`.7z.001`、`.z01…zip`）边解压边删已读完的卷，解压一个 50 卷的包不会同时占用两份磁盘空间；**RAR 这类"独立卷"**在**整包解压成功后**统一回收（RAR 每一卷都是独立归档，解码器会回头读已读过的卷，中途删会把包读坏）。可选开关，并带「保留最后 N 卷」保险丝；分卷不完整时自动禁用删除。
2. **拖拽直出（零临时文件）** —— 把文件/目录从窗口直接拖到资源管理器，边解码边写入最终路径，全程不落地临时文件。
3. **Bandizip 风格的极简界面** —— 一行工具栏 + 一行地址栏 + 一个列表 + 一行状态栏，没有多步向导。解压时显示**双进度条**（上方总进度、下方当前文件进度）。

## 下载

免安装的**便携版**与**安装版**见 [Releases](https://github.com/ldl171354/SliceArc/releases)。便携版是单文件 EXE，双击即用，设置写在 exe 同目录；安装版会注册资源管理器右键菜单，卸载时自动清理。

## 更新日志

当前版本 **0.4.4**，完整变更见 [CHANGELOG.md](CHANGELOG.md)（0.4.3 新增压缩包内图片预览、缩略图视图与更新检查，并修掉一批错误提示问题——含切片分卷缺卷静默“成功”）

## 功能一览

### 解压
- 支持 `7z.dll` 内置的全部格式处理器：7z / zip / rar / tar / gz / bz2 / xz / zst / cab / iso / wim 等
- 分卷识别：
  - **切片分卷** `.7z.001` / `.z01…zip`（一条归档流按字节切开）
  - **独立卷** `.part1.rar` / `.rar` + `.r00` / **`.part01.exe`（RAR 自解压首卷）** —— 每卷都是独立归档，靠 `IArchiveOpenVolumeCallback` 逐卷取流，不能拼接
- 加密压缩包：自动识别并弹出密码框，支持重试；错误口令与数据损坏可区分
- 路径穿越防护、zip 炸弹上限、取消
- 失败时给出「原因 + 可操作建议」，并只报告首个失败原因（避免级联错误淹没真实问题）
- 所有用户可见错误均为中文，不暴露原始错误码

### 压缩
- 输出格式：7z / zip / tar / gz / bz2 / xz / wim（`zst` 只能解压，见「已知限制」）
- 压缩级别 0–9（存储 ~ 极限），可指定分卷大小
- **向现有压缩包追加 / 更新文件**：7z / zip / tar / wim（同名就地替换）

### 其他
- **工具菜单**：压缩包完整性校验、批量重命名
- **4 套主题**：浅色 / 深色 / 柔和 / Bandizip 风格；深色主题下原生标题栏同步适配
- **文件关联 + 资源管理器右键菜单**（免管理员）：解压到当前文件夹 / 解压到「归档名」文件夹 / 解压并删除已用完的分卷 / 测试压缩包完整性 / 用本程序打开
  - Windows 11 现代一级菜单由稀疏 MSIX + COM ShellExt 提供，注册前会判断系统版本（Win10 走经典注册表菜单，跳过 UAC 提示）
- **把文件/目录拖进窗口 = 添加到当前打开的压缩包**
- **命令行模式**，供脚本与右键动词调用（`--extract` / `--extract-all` / `--test` / `--compress` / `--add-to` / `--register` / `--unregister` / `--shell-status`）
- 便携单文件 EXE（.NET 运行时与 `7z.dll` 全部内嵌），设置与日志写在 exe 同级目录，不写 `%APPDATA%`

## 项目结构

```
src/SliceArc.Core/     引擎：分卷探测、7z.dll COM 互操作、解压/压缩/追加/校验、拖拽直出
src/SliceArc.App/      WPF 界面、命令行入口、主题、右键菜单注册
src/SliceArc.ShellExt/ Windows 11 现代右键菜单的 COM shell 扩展（稀疏 MSIX）
src/Shared/            Core 与 ShellExt 共用一份的判定逻辑（如「这个名字像不像压缩包」）
tests/SliceArc.Probe/  端到端自检（造包 → 解压 → SHA256 → 删卷 → 右键菜单回读 → 拖拽协议）
tests/SliceArc.DropProbe/ 进程内真实 OLE 投递（拖出窗口 / 拖入窗口）
tools/                 打包、图标生成、CLI 端到端测试、UI 自动化检查
docs/index.html        项目落地页
docs/promo.html        22 秒宣传动画 · 横屏 16:9（单文件时基动画，逐帧可导出视频）
docs/promo-vertical.html  33 秒竖屏 9:16 版（抖音比例，全面介绍 + 右键菜单）
tools/promo-*.mjs      动画的渲染/探针工具（用 CDP 驱动无头浏览器，不依赖任何 npm 包）
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

## 许可

本项目自身代码以 [MIT 许可](LICENSE) 发布。

## 第三方组件

- `7z.dll` 来自 [7-Zip](https://www.7-zip.org/)，遵循其许可（LGPL v2.1+，含 unRAR 限制条款）。详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 已知限制

- `zst`（Zstandard）**只能解压、不能创建** —— 随包的 7-Zip 26.04 没有实现 zstd 写入（其自带的 `7z.exe` 同样报「未实现」）；`.lz4` / `.br` 则完全不支持（没有对应处理器）。
- **RAR 只读**：可以把 `.rar` 解压出来，但不能向 `.rar` 追加或更新文件。
- RAR 独立卷不支持「边解边删」，只在整包解压成功后统一回收（原因见上）。
- `--extract-all` 批量解压暂不支持对整批加密包输入统一口令（会逐个提示/跳过）。
- 归档内右键「解压选中项到…」的默认目录目前固定为「上次用过的目录」。
- 拖拽多文件时不还原空目录，只提供复制语义。
