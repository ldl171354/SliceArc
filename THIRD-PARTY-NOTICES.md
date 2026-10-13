# 第三方组件与许可

本项目**自身代码**以 [MIT 许可](LICENSE) 发布。

随程序一并分发的第三方组件如下，其许可与本项目自身许可相互独立。

---

## 7z.dll（7-Zip）

| 项 | 内容 |
|---|---|
| 文件 | `src/SliceArc.App/7z.dll`（随发布包内嵌） |
| 来源 | https://www.7-zip.org/ |
| 版权 | Copyright (C) Igor Pavlov |
| 许可 | GNU LGPL v2.1 或更高版本，**并附带 unRAR 限制条款** |
| 许可全文 | 见 `third_party/7-Zip-License.txt` |

说明：

- 本项目的压缩/解压/校验能力全部由 7-Zip 官方的 `7z.dll` 提供，本项目不自行实现任何压缩格式的解析器。
- 7-Zip 的 **unRAR 限制条款**禁止利用其 RAR 代码逆向实现 RAR 压缩算法。本项目仅通过 `7z.dll` 读取（解压）RAR 归档，不实现 RAR 压缩器。
- 如需替换为自行构建的 7-Zip，请用相同版本的 `7z.dll` 覆盖 `src/SliceArc.App/7z.dll` 后重新构建。
