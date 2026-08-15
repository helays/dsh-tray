<div align="center">

# 🐳 dsh-tray

**DeepSeek Harness — 一键托盘启动器（自包含 EXE）**

双击启动 DeepSeek Harness Web GUI 并最小化到系统托盘。托盘图标与 exe 文件图标均为 **DeepSeek 小鲸鱼**，图标已内嵌进 exe 资源——**单文件、自包含、免安装**。

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%208%2B-lightgrey)](#前置条件)
[![CSharp](https://img.shields.io/badge/language-C%23-Windows)](#)

</div>

---

## ✨ 特点

- **单文件交付**：`bin/DshTray.exe` 内嵌鲸鱼图标（托盘 + exe 文件图标），不依赖任何外部图标文件。
- **一键启动**：双击 `.bat`（或直接双击 exe）即启动 Harness Web GUI 并进入系统托盘。
- **随启随停**：托盘"退出"自动停止它**自己启动的** Harness 实例（含子进程）。
- **自动打开浏览器**：自启服务成功、页面就绪后自动打开默认浏览器（可用 `--no-open` 关闭）。
- **不误伤现有会话**：启动时若目标端口已有服务，则不重复自启，只挂托盘指向现有服务。
- **零依赖运行时**：仅需 Node.js + 已安装的 `@deepseek-ai/dsh`；编译 exe 仅需 Windows 自带 .NET Framework。

## 📦 前置条件

- **Node.js**（`DshTray.exe` 通过它调用 `@deepseek-ai/dsh` 运行服务）。
- `@deepseek-ai/dsh` 已安装在该用户的 **npx 缓存**中（工具自动在其中查找 `lib/bin.js`）。
- **首次编译 exe** 需要 Windows 自带的 .NET Framework 4.x；使用现成 exe 则仅需运行。

## 🚀 快速开始

```bat
cd dsh-tray
start-harness.bat          :: 第一次自动编译 exe, 之后直接启动托盘
```

或直接运行（无参即可）:

```bat
bin\DshTray.exe
```

启动后：鲸鱼图标出现在系统托盘（任务栏右侧），并在页面就绪后**自动打开默认浏览器**，指向 `http://127.0.0.1:3080`。

## 🖱️ 托盘操作

| 操作 | 行为 |
|---|---|
| **单击 / 双击图标** | 打开浏览器（注：单击与双击左键都会触发打开） |
| **右键 → 打开浏览器** | 同上 |
| **右键 → 重启 Harness** | 停止旧实例并强起新实例 |
| **右键 → 退出** | 停止它**自己启动的**实例（含子进程）并退出托盘 |

> 启动时会**先探测目标端口**：
> - 端口空闲 → 自启一个 Harness 实例并接管，"退出"会连同子进程一并终止。
> - 端口被占用 → 只挂托盘指向现有服务，"退出"不会误杀该外来实例。
> - 若托盘仅是「接管」状态，"重启"会尝试自启新实例，可能争用端口——此时建议直接访问既有服务地址。

## ⚙️ 自定义参数

开箱即用（默认端口 `3080`、浏览器地址 `http://127.0.0.1:3080`）。可选参数：

```
DshTray.exe --help
  --port <int>   Web 服务端口，并用于探测是否已被占用 (默认 3080)
  --bin <path>   显式指定 dsh 的 lib/bin.js(默认自动在 npx 缓存查找)
  --icon <path>  覆盖托盘图标 .ico(默认用内嵌鲸鱼图标)
  --url <url>    浏览器打开的地址(默认 http://127.0.0.1:3080)
  --no-open      启动后不自动打开浏览器
```

> ⚠️ **一致性提醒**：`--port`（服务端口/探测）与 `--url`（浏览器地址）**互不联动**——建议成对修改，
> 例如 `DshTray.exe --port 8080 --url http://127.0.0.1:8080`。

## 🏗️ 从源码构建

```bat
cd dsh-tray
powershell -NoProfile -ExecutionPolicy Bypass -File build-icon.ps1    :: 渲染鲸鱼 .ico
powershell -NoProfile -ExecutionPolicy Bypass -File build-exe.ps1     :: 编译含内嵌图标的 exe
```

产物：`bin/DshTray.exe`（自包含，含鲸鱼图标资源 + exe 文件图标）。

## 📁 目录结构

```
dsh-tray/
├─ bin/DshTray.exe        # 交付物：自包含托盘宿主
├─ src/DshTray.cs         # 托盘宿主 C# 源码
├─ assets/DeepSeekWhale.ico
├─ dsh-whale-path.txt     # 从官方 favicon 提取的鲸鱼 SVG path
├─ build-icon.ps1         # SVG path -> .ico
├─ build-exe.ps1          # csc 编译（/win32icon + /resource 嵌入图标）
├─ start-harness.bat      # 双击入口：首次构建并启动
├─ docs/CHANGELOG.md
├─ LICENSE                # MIT
└─ README.md
```

## 🛠️ 技术栈

- C# / WinForms（`.NET Framework 4.x`，Windows 自带 csc 编译）
- PowerShell 脚本（图标渲染 + 一键构建）
- 图标：DeepSeek Harness 官方 `favicon.svg` 中提取的小鲸鱼路径，经 WPF 渲染为多尺寸 `.ico`

## 📄 许可证

[MIT](LICENSE) © 2026 dsh-tray contributors

> 本项目为独立工具，与 `@deepseek-ai/dsh` 官方无关联；鲸鱼图标取自 Harness 官方 favicon。
