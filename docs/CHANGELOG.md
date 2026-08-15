# Changelog

本项目所有值得记录的变更。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added

- **自包含交付**：鲸鱼图标内嵌进 `DshTray.exe`（`/win32icon` 文件图标 + `/resource` 运行时可读资源），
  单文件即可运行，不再依赖外部图标。
- **端口接管逻辑**：启动时探测目标端口；已在监听则不重复自启，仅挂托盘指向现有服务；
  「退出」只停止本程序自己启动的实例，不误杀外来实例。
- **自动打开浏览器**：自启 Harness 成功后，等待页面就绪即自动打开默认浏览器（默认开，可用 `--no-open` 关闭）。
- **`--no-open` 参数**：关闭「启动后自动打开浏览器」。
- **GitHub 物料**：`LICENSE`(MIT)、`.gitignore`、`README.md`(徽章)、`docs/CHANGELOG.md`。

### Changed

- `start-harness.bat` 精简为启动自包含 exe（不再传 `--icon`）。
- `DshTray.cs`：托盘图标优先从内嵌资源加载，`--icon` 作外部回退。
- `build-exe.ps1`：加入 `/win32icon` + `/resource` 图标嵌入。

## [v0.1.0] - 2026-08-15 · 首个可用版本

### Added

- 双击 `start-harness.bat` 启动 DeepSeek Harness Web GUI 并最小化到系统托盘。
- 托盘宿主 `DshTray.cs / DshTray.exe`（WinForms 无窗口程序），含鲸鱼图标。
- 鲸鱼图标：从官方 `favicon.svg` 提取 SVG path 并经 WPF 渲染为多尺寸 `.ico`（`build-icon.ps1`）。
- 菜单：「打开浏览器 / 重启 Harness / 关于 / 退出」；退出时用 `taskkill /T /F` 终止自启实例进程树。
- 命令行参数：`--port` / `--bin` / `--icon` / `--url`。

---

仓库初始化历史：
- 早期纯 PowerShell 启动脚本（`start-harness.ps1`）已在开发中移除，由 EXE 托盘方案取代。
