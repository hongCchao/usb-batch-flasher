# USB 批量烧录工具

适用于 Windows x64 的 USB 存储设备批量写入工具。它可以按电脑或集线器的物理 USB 位置绑定端口，显示设备状态，并向选定设备写入磁盘镜像或分发 ZIP 文件。项目使用 C#、WPF 和 .NET 8。

> **数据安全**：镜像写入会覆盖所选设备的整个物理磁盘；ZIP 模式启用“复制前重建并格式化”时也会清除全部分区和数据。操作前请逐一核对确认窗口中的磁盘编号、型号、容量及序列号。请先用一只可以清空的 U 盘进行验证。

## 功能

- 支持 `.img`、`.iso` 原始磁盘写入；`.img.gz` 自动解压成 IMG 后写入。已烧录过的 U 盘可以再次清除并写入。
- ZIP 解压后复制到 U 盘。默认保留现有分区；可选择先重建并格式化为 exFAT、NTFS 或 FAT32。提供同名文件覆盖、跳过和询问策略。
- 初次启动为 0 个端口。插入未绑定的 USB 设备后可按提示添加端口；支持逐口识别、手动绑定、重命名、排序和删除。
- 自动及手动刷新设备状态。端口分别显示未插入、已插入和烧录失败状态；设备拔出再插入后重新检测。支持多选、全选、并发任务、进度和日志。
- 记录最近使用的文件及端口设置。程序目录中的 `cache` 复用已验证的解压结果；“清理缓存”清除解压缓存和文件选择记录。

## 构建与运行

在 Windows x64 上打开 PowerShell，在项目根目录执行：

```powershell
.\build.ps1
```

脚本可使用系统 `PATH` 中的 .NET 8 或更新版 SDK，也可使用项目 `.tools` 目录中的 SDK；两者都不存在时，会从微软官方下载 .NET 8 SDK 并校验 SHA-512。首次下载 SDK 或还原 NuGet 依赖时需要网络。发布结果是 `dist\USB批量烧录工具.exe`，使用自包含单文件发布，目标电脑无需另装 .NET 运行时。如果只允许使用已有 SDK，可执行 `.\build.ps1 -NoBootstrap`；没有可用 SDK 时，该命令会报错而不会下载。

将生成的 EXE 放在可写入的目录，再以管理员身份运行。程序会在 EXE 所在目录创建 `config`、`cache`、`logs`；请勿将这些运行时数据提交到仓库。更换 EXE 时保留这些目录即可保留设置和缓存。首次启动按提示添加端口，选择源文件并勾选目标，核对最终确认窗口后开始操作。

仓库还包含用于特定本机 PowerShell/WPF 运行时的离线构建脚本。它们依赖预装组件，面向现有开发环境；GitHub 源码构建请使用上面的 `build.ps1`。

## 端口识别

端口绑定优先使用 Windows 提供的 USB Location Path，因此同一接口换一只 U 盘后通常仍能匹配。某些设备或控制器无法提供稳定的位置路径，软件会保留手动绑定方式。集线器改插到另一个电脑接口、USB 2.0/3.0 路径切换或硬件拓扑变化时，可能需要重新绑定或添加路径别名。设备序列号用于展示和安全校验，不应当成物理端口号。

软件会拒绝已识别的系统盘、启动盘和不满足安全检查的设备；但 Windows 对 USB 硬盘、SSD、读卡器和某些适配器的报告可能因硬件而异，最终仍应由操作者确认目标。ISO 以原始字节写入，能否启动取决于 ISO 是否支持这种方式。源文件和程序不要放在目标 U 盘上。

## 测试

仓库中的 `tests` 包含 WPF 离屏加载、设置持久化和服务逻辑测试。它们使用临时目录及模拟设备，不会格式化真实磁盘。这些测试针对离线 Roslyn 程序集，**不能直接使用上面的单文件 EXE 作为测试输入**。测试环境需要 Windows x64、PowerShell 7 (`pwsh`) 以及同一 PowerShell 运行目录中的 Roslyn、WPF 桌面程序集和 `System.Management.dll`。满足这些条件后，在项目根目录执行：

```powershell
pwsh -NoProfile -File .\build-offline.ps1 -OutputDirectory .\artifacts\test-build
pwsh -NoProfile -STA -File .\tests\SmokeTest.ps1 -AssemblyPath .\artifacts\test-build\UsbBatchTool.Offline.dll
pwsh -NoProfile -File .\tests\SettingsTests.ps1 -AssemblyPath .\artifacts\test-build\UsbBatchTool.Offline.dll
pwsh -NoProfile -File .\tests\ServiceTests.ps1 -AssemblyPath .\artifacts\test-build\UsbBatchTool.Offline.dll
```

这些测试不能代替真实硬件验收。正式使用前应测试所用电脑和集线器的各个端口、重新插拔、镜像读回、重复烧录及 ZIP 格式化流程。

## 目录

| 路径 | 用途 |
| --- | --- |
| `src/UsbBatchTool/` | WPF 应用源码 |
| `app.ico`、`已插入.png`、`未插入.png`、`烧录失败.png` | 构建时嵌入的界面素材 |
| `build.ps1` | Windows x64 单文件发布 |
| `tests/` | 自动化测试 |
| `.tools/`、`dist/` | 本地 SDK 和生成结果，不纳入版本控制 |

## 参与开发

欢迎通过 Issue 报告设备识别或写入问题。请提供 Windows 版本、USB 控制器或集线器型号、设备型号、脱敏后的日志及复现步骤；不要上传包含个人文件、设备序列号或完整本机路径的配置与日志。

## 许可

项目代码及仓库内的图标、状态图片均按 [MIT 协议](LICENSE)发布。
