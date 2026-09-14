# Codex Meter

[English](README.md) | [简体中文](README.zh-CN.md)

Codex Meter is a lightweight native usage widget for Codex on macOS and Windows. It stays out of the way as a draggable 66 × 66 icon, expands when you hover over it, and gives you a quick view of account limits, reset credits, today's activity, and recent token usage.

![Codex Meter in light mode](assets/screenshots/codex-meter-light.jpg)

## Download and install

- [macOS Apple Silicon installer — Codex Meter v1.4.7](https://github.com/Liz-509/Codex-Meter/releases/download/v1.4.7/Codex-Meter-macOS-arm64-v1.4.7.dmg)
- [Windows 10/11 x64 installer — Codex Meter v1.4.2](https://github.com/Liz-509/Codex-Meter/releases/download/v1.4.2/Codex-Meter-Windows-x64-v1.4.2.exe)

On macOS, open the downloaded DMG and drag `Codex Meter` to the Applications shortcut, then launch it from Applications. On Windows, open the downloaded setup EXE and follow the installer; you can choose the installation location and whether to create a desktop shortcut. The lightweight setup downloads .NET 8 Desktop Runtime and the shared Windows App Runtime only when they are missing, so an internet connection may be required on first install. It then adds the app to the Start menu and registers it in **Installed apps**. Uninstalling Codex Meter does not remove shared runtimes and can preserve or remove the app's local preferences without touching Codex sessions. Intel Mac users can build an installer from source.

Because the macOS app is not notarized, macOS may ask you to confirm the first launch. Control-click the app, choose **Open**, then confirm **Open**.

## Highlights

- See the five-hour and weekly Codex limits, including the percentage remaining and the next reset time.
- Track today's tokens and conversation turns without opening Codex.
- Open account-wide 7/30/90-day token trends and heatmaps, with local session history filling gaps when account data is unavailable or delayed.
- Break down tokens and turns by project and Codex task on this computer; on macOS, optionally include currently connected Codex SSH servers from Settings, then export a rolling seven-day Markdown or CSV report.
- Keep the five-hour allowance in the macOS menu bar or Windows system tray, forecast exhaustion from recent pace, and opt into threshold, exhaustion, and recovery notifications.
- Browse today's conversation turns from this computer and, after enabling SSH conversation monitoring, currently connected servers, merged by Codex task and labeled with the corresponding task name when available.
- On macOS, see matching rings for the five-hour and weekly allowances, then watch current-answer and conversation token totals update in the full-width card below with context remaining and smooth number transitions.
- Redeem an available reset credit after an explicit confirmation. Codex Meter never consumes a credit automatically.
- Drag the compact icon anywhere, or pin the expanded panel so it stays open. The pin preference is remembered across restarts.
- Follow the system appearance or select light or dark mode.
- Keep the widget above other windows on macOS and Windows.
- Run entirely on your device with no analytics, separate backend, credential storage, or Codex lifecycle hook. Launch at login is optional and disabled until you enable it in Settings.

## Interface tour

| Usage trends | Projects and tasks |
| --- | --- |
| ![Seven-day token trend and quota forecast](assets/screenshots/usage-trend.jpg) | ![Projects and their Codex tasks](assets/screenshots/projects-tasks.jpg) |
| **Weekly report and export** | **Today's conversations** |
| ![Rolling seven-day report with Markdown and CSV export](assets/screenshots/weekly-report.jpg) | ![Today's conversations grouped by Codex task](assets/screenshots/today-conversations.jpg) |
| **Context health** | **Settings and notifications** |
| ![Recent context-window health and compaction details](assets/screenshots/context-health.jpg) | ![Launch-at-login, notifications, and menu-bar settings](assets/screenshots/settings-notifications.jpg) |

The interface examples above use sample data. Account and task data from your installation stays on your device.

## How to use Codex Meter

1. Launch Codex Meter. The widget starts near the top-right corner of the current screen as a compact icon.
2. Drag the icon to a convenient position. Hover over it to open the full panel.
3. Read the two quota indicators for your five-hour and weekly limits. The compact icon reflects the five-hour limit with an animated liquid level.
4. Click **Today Tokens** to open the seven-day usage chart, or **Today Conversations** to inspect today's local turns.
5. Use the controls in the header to change the theme, refresh immediately, pin or unpin the panel, collapse it, or quit the app.

By default, Codex Meter updates the current answer and context every 2 seconds, account and local statistics every 30 seconds, and enabled SSH data every 60 seconds. The macOS Settings panel offers independent presets for all three groups; SSH is limited to 30 seconds or slower and warns about network, power, and remote-host load at the fastest setting. At startup, the app retries rapidly until data arrives. It also refreshes when you press the refresh button and after a reset-credit request. When the panel is not pinned, it collapses after the pointer leaves; open dialogs and active drags keep it expanded. The native host keeps expansion inside the current screen's usable area, including when the icon is close to an edge.

## What the numbers mean

| Item | What it shows | Data source |
| --- | --- | --- |
| Account plan | The plan type reported for the connected Codex account | Codex App Server account limits |
| Five-hour limit | Remaining percentage and the time until the short usage window resets | Codex App Server account limits |
| Weekly limit | Remaining percentage and the time until the weekly window resets | Codex App Server account limits |
| Reset credits | Credits currently available to reset a supported rate limit | Codex App Server account data |
| Today Tokens | Account-wide tokens for the local calendar day when available, otherwise locally recorded structured session usage | Account usage buckets with a local fallback |
| Today Conversations | User conversation turns started today, with prompt previews and per-turn token totals when present | Structured session events from this computer and opted-in currently connected SSH servers |
| 7/30/90-day trends | The most recent 90 local calendar days with selectable ranges, using account buckets where available and local data to fill missing dates | Account usage buckets plus local sessions |
| Projects and tasks | Tokens, turns, and activity grouped by session working directory, server, and task identifier | Session metadata from this computer and opted-in currently connected SSH servers |
| Usage forecast | A trend estimate derived from remaining-percentage observations in the current quota cycle | Lightweight local quota snapshots |
| Current-answer / conversation tokens / context health | macOS follows the current answer about every two seconds, sums the latest value for every turn into the conversation total, and retains window occupancy and compaction data for the 20 most recent measurable tasks | Local task metadata and structured session events |

Quota indicators are yellow below 20% remaining and red below 10%. Reset times are displayed as relative countdowns. The Today Conversations dialog groups turns by Codex task ID, merging context windows from before and after compaction, and falls back to context-window grouping only for legacy payloads without a task ID. Each group shows its time range and token total and can be expanded to inspect individual turns. It uses the Codex task name when the local App Server can match one; otherwise it falls back to the first local prompt.

On macOS, current-answer usage prioritizes `token_usage_record.turn_token_usage.total_tokens`; once that authoritative record appears for a turn, the conversation-wide total can no longer overwrite it. Older logs use the change in `token_count.info.total_token_usage.total_tokens` since the turn began. The conversation total sums the latest value for every turn in the task, so a new question or context compaction does not reset it. Large formatted values automatically reduce their font size instead of showing an ellipsis. The first row shows matching rings for the five-hour and weekly allowances, with the full-width current-answer card below. Refresh presets appear in a collapsed settings row by default. Usage below 60% is Healthy, 60–79% is Attention, 80–89% is High, and 90% or above is Critical. Current-answer usage, conversation usage, context occupancy, today's usage, and account quota are separate metrics. SSH tasks use an independent interval of at least 30 seconds; Windows does not yet expose answer/conversation totals or refresh settings.

## Reset credits

When at least one reset credit is available, the **Reset Credits** card becomes actionable. Selecting it opens a confirmation dialog. Only an explicit confirmation sends `account/rateLimitResetCredit/consume` to the local Codex App Server with a unique idempotency key.

Codex Meter cannot purchase credits or resets, and it never redeems one in the background. After the request finishes, the widget reports the returned outcome and refreshes all usage data.

## Themes and motion

The theme control cycles through **Follow System**, **Light**, and **Dark**. The selected theme is saved locally. The widget includes animated quota fills and small button interactions; compact motion is paused when the host is hidden, the display is asleep, the session is locked, or the page is not visible to avoid unnecessary work.

![Codex Meter in dark mode](assets/screenshots/codex-meter-dark.jpg)

## Reliability and local fallback

Codex Meter reads local session statistics first, so today's activity can appear while account limits are still syncing. Until the first successful account sync, the native host keeps retrying at short intervals. If a later account request fails, local token and conversation details remain available, the panel shows the sync error, and the native host retries after short delays. Regular refreshes continue afterward at the selected general-data interval.

Local structured session files are scanned from `~/.codex/sessions`, or from `$CODEX_HOME/sessions` when `CODEX_HOME` is set. On macOS, SSH conversation monitoring is off by default. Codex Meter shows a one-time guide when it detects an active Codex SSH connection, and the feature can be enabled at any time in Settings. When enabled, it first filters Codex's saved hosts against the system's established SSH connections, then reuses the matching SSH alias, port, and identity to aggregate the same directory with a non-interactive read-only command. Each refresh can therefore take longer, but saved hosts that are not currently connected are never contacted. When disabled, refreshes skip remote session collection entirely. The app retains only the latest successful aggregate in memory; remote session files are never copied or persistently cached locally. Remote hosts require `python3`. Codex Meter analyzes up to 90 days; dates and “today” use the current device's local time zone.

On both platforms, up to 14 days of lightweight quota-percentage snapshots are stored in the platform's local application-data folder for trend forecasting. They contain no prompts, file contents, or credentials. Notifications remain off until the user opts in and grants system permission.

## Platform behavior

| | macOS | Windows |
| --- | --- | --- |
| Supported systems | macOS 13 or later, Apple Silicon or Intel | Windows 10 22H2 or Windows 11, x64 |
| Window behavior | Floats above other windows and appears across Spaces and full-screen apps | Stays on top on the current virtual desktop |
| Background access | Remains clickable when another app is active | Remains clickable when another app is active |
| System integration | Dock presence plus an optional menu-bar percentage, local notifications, and report export | Always-available system tray with optional percentage icon, quota/forecast details, local notifications, settings, refresh, report export, and panel controls |
| Multiple launches | macOS handles the running app normally | A single-instance guard prevents duplicates and asks the existing instance to show itself |
| Web runtime | Uses the system WKWebView | Uses Microsoft Edge WebView2 and offers the official download page when the runtime is missing |

Windows does not expose a stable public equivalent of macOS “all Spaces,” and exclusive full-screen games may cover the widget.

## Requirements

- macOS 13 or later on Apple Silicon or Intel, or Windows 10 22H2 / Windows 11 on x64.
- ChatGPT/Codex desktop installed and signed in, or a local `codex` executable available in a supported location.
- Microsoft Edge WebView2 Runtime on Windows. Codex Meter detects a missing runtime and offers the official download page.
- Xcode Command Line Tools for macOS source builds:

```bash
xcode-select --install
```

- .NET 8 SDK for Windows source builds.

## How Codex Meter gets its data

Codex Meter starts the local Codex App Server and uses its account endpoints for rate limits, reset times, reset credits, and optional account-wide daily usage buckets. It also requests the recent thread list so local context windows can use their matching Codex task names.

Session statistics come from structured JSONL events in Codex session directories, including currently connected Codex SSH hosts on macOS when SSH conversation monitoring is enabled. Codex Meter uses them to calculate daily token totals, count today's user turns, group turns into context windows, read current window occupancy and compaction events, and display prompt previews. Account history takes precedence for dates returned by the App Server; missing or delayed dates are filled from local and connected SSH history so today's value does not unnecessarily drop to zero.

The first Codex App Server launch may be slower. Codex Meter displays local statistics first and retries account requests automatically when needed.

## Finding Codex

### macOS

Codex Meter checks these locations in order:

1. The `CODEX_BINARY` or `CODEX_CLI_PATH` environment variable.
2. `/Applications/ChatGPT.app/Contents/Resources/codex`.
3. `~/Applications/ChatGPT.app/Contents/Resources/codex`.
4. `~/.codex/plugins/.plugin-appserver/codex`.
5. `/opt/homebrew/bin/codex` and `/usr/local/bin/codex`.

### Windows

Codex Meter checks `CODEX_BINARY`, `CODEX_CLI_PATH`, `CODEX_INSTALL_DIR`, `PATH`, official standalone installs, relocated Codex Desktop runtimes, the Microsoft Store app cache, the npm global command directory, Windows app aliases, and common ChatGPT installation directories.

Native Windows Codex and ChatGPT share `%USERPROFILE%\.codex` by default. Set `CODEX_HOME` only when you intentionally keep sessions elsewhere. macOS honors the same override.

## Build from source

### macOS

Download or clone the repository, then double-click `Install.command`. It builds for the current Mac and installs the app at:

```text
~/Applications/Codex Meter.app
```

The app does not start automatically. Open it manually from the Applications folder.

To build a distributable installer:

```bash
scripts/build-macos.sh
```

The DMG is written to `outputs/Codex-Meter-macOS-<architecture>-v<version>.dmg`, while the intermediate app remains at `build/Codex Meter.app`. The build script detects the current CPU architecture and an available macOS SDK. Pass a version as the first argument when needed. Advanced users can override defaults with `CODEX_USAGE_ARCH`, `CODEX_USAGE_SDK`, `SWIFTC`, and `MACOSX_DEPLOYMENT_TARGET`.

### Windows

Install the .NET 8 SDK, clone the repository, and run the following command in PowerShell:

```powershell
.\scripts\build-windows.ps1
```

The framework-dependent x64 app is written to `build\windows\win-x64`, and the lightweight graphical installer is written to `outputs\Codex-Meter-Windows-x64-v1.4.2.exe`. The installer embeds only the compressed app payload and has a 10 MB build-time size budget. On first install it downloads .NET 8 Desktop Runtime and Windows App Runtime 2.4 only if they are missing. Open the EXE to install Codex Meter for the current Windows account. The installer adds the app to the Start menu and registers it with Windows **Installed apps** for upgrades and removal.

## Embeddable web component

The repository also includes the reusable `usage-widget.js`. To preview it locally:

```bash
python3 -m http.server 4173
```

Then open `http://localhost:4173`.

A native host can implement `window.codexMeterBridge` with `getUsage`, `consumeReset`, `resize`, `quit`, notification, SSH-monitoring, and refresh-setting methods, `setMenuBarVisible`, and `exportReport`. Native drag support can additionally provide `beginDrag`. Reset, notification-setting, SSH-monitoring-setting, refresh-setting, export, and live-context results are delivered through `window.codexResetResult(payload)`, `window.codexNotificationSettingsResult(payload)`, `window.codexRemoteSessionSettingsResult(payload)`, `window.codexRefreshSettingsResult(payload)`, `window.codexExportResult(payload)`, and `window.updateCodexContextHealth(payload)`. The legacy `window.codexUsageBridge.getUsage()` hook remains supported. A web host can provide a matching `GET /api/usage`, or push data directly:

```js
window.updateCodexUsage(payload);
window.recordCodexTurn({ inputTokens: 1200, outputTokens: 480 });
```

Hosts can optionally include `today.tokenSource`, `today.conversations`, and `history.dailyTokens` in the usage payload to populate the detail dialogs. Conversation entries may include `contextWindowId` and `threadName`; the widget falls back to `threadId` and then `turnId` when grouping older payloads, and to the first prompt when a task name is unavailable. Older payloads remain supported and show an empty detail state when these fields are absent.

Extended hosts can also provide `insights.projects`, `insights.tasks`, `contextHealth.sessions`, `forecast.primary`, `forecast.secondary`, and capability flags. Context rows include task/project identifiers, optional `conversationTokens`, `currentTurnId`, `currentTurnTokens`, `currentTurnActive`, `usedTokens`, `maxTokens`, `usedPercent`, `remainingPercent`, `status`, `lastActive`, and `compactions`. `capabilities.contextHealth` enables the context entry; adding `capabilities.currentConversationTokens` makes its main card show the current-answer and conversation totals. `capabilities.refreshSettings` enables the live-answer, general-data, and SSH refresh presets. Project and task entries may use `projectKind` with `project` or `non_project`; desktop hosts group sessions without a resolvable Git root under “非项目中对话” (non-project conversations). A `partial` refresh payload is used as a cold-start placeholder and does not replace stable history, task, forecast, report, or context-health data after a complete snapshot has arrived. Without `capabilities.extendedInsights`, the shared widget retains its legacy seven-day chart so older embedded hosts do not expose unavailable controls.

## Privacy

Codex Meter has no analytics or separate backend, does not store account credentials, and does not upload local session content. Prompt previews, project statistics, quota snapshots, and exported reports stay on the device. See [PRIVACY.md](PRIVACY.md).

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for build checks and platform-specific testing guidance.

## License

[MIT](LICENSE)

Codex Meter is an unofficial community project and is not affiliated with or endorsed by OpenAI. Codex, ChatGPT, and OpenAI are trademarks of their respective owners.
