# Codex Meter

[English](README.md) | [简体中文](README.zh-CN.md)

Codex Meter 是一款适用于 macOS 和 Windows 的轻量级 Codex 原生用量悬浮组件。平时它以一个可拖动的 66 × 66 图标停留在桌面上，鼠标悬停后展开，让你快速查看账户额度、重置次数、今日活动和近期 Token 用量。

![Codex Meter 浅色模式](assets/screenshots/codex-meter-light.jpg)

## 功能亮点

- 查看 Codex 5 小时额度和每周额度，包括剩余百分比与下次重置时间。
- 无需打开 Codex，即可掌握今日 Token 用量和对话轮次。
- 查看账户级最近 7 天 Token 趋势；账户数据缺失或延迟时，由本地会话历史补齐。
- 浏览今天的本地对话轮次，按上下文窗口分组，并在可用时显示对应的 Codex 任务名称。
- 在明确确认后使用可用的额度重置次数；Codex Meter 绝不会自动消耗重置次数。
- 随意拖动紧凑图标，或固定展开面板使其保持打开；固定状态会在重启后保留。
- 跟随系统外观，或手动选择浅色、深色模式。
- 在 macOS 和 Windows 上保持悬浮于其他窗口之上。
- 所有处理均在本机完成，无分析统计、独立后端、凭据存储、登录项或 Codex 生命周期钩子。

## 如何使用 Codex Meter

1. 启动 Codex Meter。组件会以紧凑图标出现在当前屏幕的右上角附近。
2. 将图标拖到方便的位置，鼠标悬停即可展开完整面板。
3. 查看 5 小时和每周额度。紧凑图标会通过动态液面反映 5 小时额度。
4. 点击 **今日 Tokens** 打开最近 7 天用量图表，或点击 **今日对话** 查看今天的本地对话轮次。
5. 使用顶部按钮切换主题、立即刷新、固定或取消固定面板、折叠面板或退出应用。

Codex Meter 每分钟自动刷新一次，也会在启动、点击刷新按钮和完成额度重置请求后刷新。当面板未固定时，鼠标移开后会自动折叠；打开的对话框和正在进行的拖动会让面板保持展开。即使图标靠近屏幕边缘，原生宿主也会确保展开后的面板处于当前屏幕的可用区域内。

## 各项数据的含义

| 项目 | 显示内容 | 数据来源 |
| --- | --- | --- |
| 账户方案 | 当前连接的 Codex 账户所报告的方案类型 | Codex App Server 账户额度 |
| 5 小时额度 | 短周期用量窗口的剩余百分比和距离重置的时间 | Codex App Server 账户额度 |
| 每周额度 | 每周用量窗口的剩余百分比和距离重置的时间 | Codex App Server 账户额度 |
| 重置次数 | 当前可用于重置受支持额度的次数 | Codex App Server 账户数据 |
| 今日 Tokens | 优先显示本地日历当天的账户级 Token；不可用时显示本机结构化会话记录 | 账户用量桶，本地数据作为回退 |
| 今日对话 | 今天开始的用户对话轮次，以及可用的提示词预览和每轮 Token | 本机结构化会话事件 |
| 最近 7 天 | 最近 7 个本地日历日；优先使用账户数据，并用本地数据补齐缺失日期 | 账户用量桶与本地会话 |

额度低于 20% 时指示器变为黄色，低于 10% 时变为红色。重置时间以相对倒计时显示。“今日对话”窗口按上下文分组，展示每组的时间范围和 Token 总数，并可展开查看单轮详情。如果本地 App Server 能够匹配任务，就会使用 Codex 任务名称；否则回退到第一条本地提示词。

## 额度重置次数

至少有一次可用重置次数时，**重置次数**卡片才可操作。点击后会打开确认对话框；只有明确确认，Codex Meter 才会使用唯一的幂等键向本地 Codex App Server 发送 `account/rateLimitResetCredit/consume` 请求。

Codex Meter 无法购买额度或重置次数，也绝不会在后台自动使用。请求完成后，组件会显示 App Server 返回的结果，并刷新全部用量数据。

## 主题与动画

主题按钮会在**跟随系统**、**浅色**和**深色**之间循环，选择结果保存在本机。组件包含额度填充动画和按钮微交互；当宿主被隐藏、显示器休眠、会话锁定或页面不可见时，紧凑模式动画会暂停，以减少不必要的资源占用。

![Codex Meter 深色模式](assets/screenshots/codex-meter-dark.jpg)

## 可靠性与本地回退

Codex Meter 会先读取本地会话统计，因此在账户额度仍在同步时，今日活动就可以先行显示。如果账户请求失败，本地 Token 和对话详情仍然可用，面板会显示同步错误，原生宿主会在短暂延迟后自动重试；此后仍会继续进行常规的每分钟刷新。

结构化会话文件来自 `~/.codex/sessions`；设置 `CODEX_HOME` 后则读取 `$CODEX_HOME/sessions`。刷新时会缓存未变化的文件。“今日”和日期范围均以设备当前的本地时区为准。

## 平台行为

| | macOS | Windows |
| --- | --- | --- |
| 支持系统 | macOS 13 或更高版本，Apple Silicon 或 Intel | Windows 10 22H2 或 Windows 11，x64 |
| 窗口行为 | 悬浮于其他窗口之上，并出现在所有 Spaces 和全屏应用中 | 在当前虚拟桌面保持置顶 |
| 后台交互 | 其他应用处于活动状态时仍可点击 | 其他应用处于活动状态时仍可点击 |
| 系统集成 | 标准应用窗口和 Dock 项目 | 系统托盘提供**显示 Codex Meter**和**退出**菜单；双击托盘图标可显示面板 |
| 重复启动 | 由 macOS 正常处理已运行的应用 | 单实例保护会阻止重复进程，并通知已有实例显示面板 |
| Web 运行时 | 使用系统 WKWebView | 使用 Microsoft Edge WebView2；缺少运行时时会提供官方下载入口 |

Windows 没有与 macOS“所有 Spaces”对应的稳定公开能力，独占全屏游戏也可能遮挡组件。

## 下载与安装

- [macOS Apple Silicon 安装包 — Codex Meter v1.4.0](https://github.com/Liz-509/Codex-Meter/releases/download/v1.4.0/Codex-Meter-macOS-arm64-v1.4.0.dmg)
- [Windows 10/11 x64 — Codex Meter v1.4.0](https://github.com/Liz-509/Codex-Meter/releases/download/v1.4.0/Codex-Meter-Windows-x64-v1.4.0.zip)

macOS 用户打开下载的 DMG，将 `Codex Meter` 拖到“应用程序”快捷方式，再从“应用程序”中启动。Windows 用户解压后直接运行 `Codex Meter.exe`，无需安装或管理员权限。Intel Mac 用户可以从源码构建安装包。

由于 macOS 应用尚未公证，首次启动时系统可能要求确认。按住 Control 点击应用，选择**打开**，然后再次确认**打开**。

## 系统要求

- Apple Silicon 或 Intel 的 macOS 13 及更高版本，或 x64 的 Windows 10 22H2 / Windows 11。
- 已安装并登录 ChatGPT/Codex 桌面版，或在受支持位置存在本地 `codex` 可执行文件。
- Windows 需要 Microsoft Edge WebView2 Runtime；缺失时 Codex Meter 会检测并提供官方下载入口。
- 从源码构建 macOS 版本需要 Xcode Command Line Tools：

```bash
xcode-select --install
```

- 从源码构建 Windows 版本需要 .NET 8 SDK。

## Codex Meter 如何获取数据

Codex Meter 会启动本地 Codex App Server，通过账户接口读取额度、重置时间、重置次数，以及可选的账户级每日用量桶；它还会请求近期任务列表，以便为本地上下文窗口匹配对应的 Codex 任务名称。

本地会话统计来自 Codex 会话目录中的结构化 JSONL 事件。Codex Meter 使用这些事件计算每日 Token 总量、统计今日用户轮次、按上下文窗口分组，并显示本地提示词预览。对于 App Server 已返回的日期，账户历史优先；缺失或延迟的日期则由本地历史补齐，避免今日数值不必要地归零。

Codex App Server 第一次启动可能较慢。Codex Meter 会先显示本地统计，并在需要时自动重试账户请求。

## 查找 Codex

### macOS

Codex Meter 按以下顺序检查：

1. `CODEX_BINARY` 或 `CODEX_CLI_PATH` 环境变量。
2. `/Applications/ChatGPT.app/Contents/Resources/codex`。
3. `~/Applications/ChatGPT.app/Contents/Resources/codex`。
4. `~/.codex/plugins/.plugin-appserver/codex`。
5. `/opt/homebrew/bin/codex` 和 `/usr/local/bin/codex`。

### Windows

Codex Meter 会检查 `CODEX_BINARY`、`CODEX_CLI_PATH`、`CODEX_INSTALL_DIR`、`PATH`、官方独立安装位置、迁移后的 Codex Desktop 运行时、Microsoft Store 应用缓存、npm 全局命令目录、Windows 应用别名和常见 ChatGPT 安装目录。

原生 Windows Codex 和 ChatGPT 默认共用 `%USERPROFILE%\.codex`。只有在有意将会话保存在其他位置时才需要设置 `CODEX_HOME`；macOS 也支持相同的覆盖方式。

## 从源码构建

### macOS

下载或克隆仓库，然后双击 `Install.command`。脚本会为当前 Mac 构建应用，并安装到：

```text
~/Applications/Codex Meter.app
```

应用不会自动启动，请从“应用程序”文件夹手动打开。

如需构建可分发的安装包：

```bash
scripts/build-macos.sh
```

DMG 安装包会生成到 `outputs/Codex-Meter-macOS-<架构>-v<版本>.dmg`，中间应用仍保留在 `build/Codex Meter.app`。构建脚本会检测当前 CPU 架构和可用的 macOS SDK；如需指定版本，可将版本号作为第一个参数传入。高级用户可以通过 `CODEX_USAGE_ARCH`、`CODEX_USAGE_SDK`、`SWIFTC` 和 `MACOSX_DEPLOYMENT_TARGET` 覆盖默认值。

### Windows

安装 .NET 8 SDK、克隆仓库，然后在 PowerShell 中运行：

```powershell
.\scripts\build-windows.ps1
```

自包含 x64 应用会生成到 `build\windows\win-x64`，便携压缩包会生成到 `outputs\Codex-Meter-Windows-x64-v1.4.0.zip`。解压后运行 `Codex Meter.exe`；无需安装或管理员权限。

## 可嵌入的 Web 组件

仓库还包含可复用的 `usage-widget.js`。如需在本机预览：

```bash
python3 -m http.server 4173
```

然后打开 `http://localhost:4173`。

原生宿主可以实现 `window.codexMeterBridge`，提供 `getUsage`、`consumeReset`、`resize` 和 `quit`；如需原生拖动支持，还可以提供 `beginDrag`。重置结果通过 `window.codexResetResult(payload)` 返回。旧版 `window.codexUsageBridge.getUsage()` 钩子仍受支持。Web 宿主可以提供匹配的 `GET /api/usage`，也可以直接推送数据：

```js
window.updateCodexUsage(payload);
window.recordCodexTurn({ inputTokens: 1200, outputTokens: 480 });
```

宿主可以在用量载荷中选择性提供 `today.tokenSource`、`today.conversations` 和 `history.dailyTokens`，以填充详情对话框。对话条目可以包含 `contextWindowId` 和 `threadName`；对旧载荷分组时，组件会依次回退到 `threadId` 和 `turnId`，任务名称不可用时则使用第一条提示词。旧版载荷仍然兼容；缺少这些字段时，详情界面会显示为空状态。

## 隐私

Codex Meter 没有分析统计或独立后端，不存储账户凭据，也不会上传本地会话内容。提示词预览只会保留在本地应用中，不会发送到任何 Codex Meter 基础设施。详见 [PRIVACY.md](PRIVACY.md)。

## 参与贡献

欢迎提交 Issue 和 Pull Request。构建检查与跨平台测试要求请参阅 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 许可证

[MIT](LICENSE)

Codex Meter 是非官方社区项目，与 OpenAI 无隶属或认可关系。Codex、ChatGPT 和 OpenAI 是其各自所有者的商标。
