# Codex Meter

A lightweight native usage widget for Codex on macOS and Windows. It starts as a draggable icon, expands on hover, and collapses when the pointer leaves.

![Codex Meter in light mode](assets/screenshots/codex-meter-light.jpg)

## Download

- [macOS Apple Silicon — Codex Meter v1.3.0](https://github.com/Liz-509/Codex-Meter/releases/download/v1.3.0/Codex-Meter-macOS-arm64-v1.3.0.zip)
- [Windows 10/11 x64 — Codex Meter v1.3.0](https://github.com/Liz-509/Codex-Meter/releases/download/v1.3.0/Codex-Meter-Windows-x64-v1.3.0.zip)

Unzip the archive. On macOS, move `Codex Meter.app` to your Applications folder and open it. On Windows, run `Codex Meter.exe` directly; installation and administrator access are not required. Intel Mac users can build from source.

Because the app is not notarized, macOS may ask you to confirm the first launch. Control-click the app, choose **Open**, then confirm **Open**.

## Features

- Shows the five-hour limit, weekly limit, reset times, and available reset credits.
- Lets you redeem an available reset credit after an explicit confirmation; Codex Meter never redeems one automatically.
- Uses a live liquid level in the compact icon and changes quota indicators to yellow below 20% and red below 10%.
- Totals today's tokens and conversations in the device's local time zone.
- Opens an account-wide seven-day token chart from the Today Tokens card, with a local-history fallback when account buckets are unavailable.
- Opens today's local conversation turns from the Today Conversations card, grouped into expandable context windows and named with the corresponding Codex task name when available.
- Supports automatic, light, and dark themes.
- Keeps the expanded panel inside the current screen and opens it from the hovered icon position near screen edges.
- Floats above other windows; the macOS build appears across Spaces and full-screen apps.
- Remains interactive when another app is in front.
- Includes animated quota indicators and button micro-interactions.
- Runs locally without login items or Codex lifecycle hooks.

## Appearance

Codex Meter follows the system appearance by default and can also be locked to light or dark mode.

![Codex Meter in dark mode](assets/screenshots/codex-meter-dark.jpg)

## Requirements

- macOS 13 or later on Apple Silicon or Intel, or Windows 10 22H2 / Windows 11 on x64.
- ChatGPT/Codex desktop installed and signed in, or a local `codex` executable available on `PATH`.
- Windows builds require the Microsoft Edge WebView2 Runtime. Codex Meter detects a missing runtime and offers the official download page.
- macOS source builds require Xcode Command Line Tools:

```bash
xcode-select --install
```

## Build from source

### macOS

Download or clone the repository, then double-click `Install.command`. It builds for the current Mac and installs the app at:

```text
~/Applications/Codex Meter.app
```

The app does not start automatically. Open it manually from the Applications folder.

To build without installing:

```bash
scripts/build-native.sh
```

The app is written to `build/Codex Meter.app`. The build script detects the current CPU architecture and an available macOS SDK. Advanced users can override defaults with `CODEX_USAGE_ARCH`, `CODEX_USAGE_SDK`, `SWIFTC`, and `MACOSX_DEPLOYMENT_TARGET`.

### Windows

Install the .NET 8 SDK, clone the repository, and run the following command in PowerShell:

```powershell
.\scripts\build-windows.ps1
```

The self-contained x64 app is written to `build\windows\win-x64`, and the portable archive is written to `outputs\Codex-Meter-Windows-x64-v1.3.0.zip`. Extract the archive and run `Codex Meter.exe`; installation and administrator access are not required.

The Windows app stays on top on the current virtual desktop and includes a system tray menu. Its executable, taskbar window, and tray entry use the same Codex Meter icon. Windows does not expose a stable public equivalent of macOS “all Spaces,” and exclusive full-screen games may cover the widget.

## Data sources

- Limits, reset times, and reset credits come from the Codex App Server account endpoints.
- Confirmed reset requests are sent directly to the Codex App Server with a unique idempotency key. They use an existing reset credit and cannot purchase credits or resets.
- Account-wide daily token history comes from the optional `dailyUsageBuckets` returned by the Codex App Server; missing or delayed dates are filled from local structured session events so today's card is not reset to zero while account data catches up.
- Today's conversation count, prompt previews, and per-turn token totals are calculated locally from structured events in `~/.codex/sessions` (or `$CODEX_HOME/sessions` when configured). Context group names come from the local Codex App Server's `thread/list` result and fall back to the first local prompt when unavailable. Prompt previews stay inside the local app and are never sent to Codex Meter infrastructure.

The first Codex App Server launch may be slower. Codex Meter displays local statistics first and retries limit requests automatically when needed.

## Finding Codex

Codex Meter checks these locations in order:

1. The `CODEX_BINARY` or `CODEX_CLI_PATH` environment variable.
2. `/Applications/ChatGPT.app/Contents/Resources/codex`.
3. `~/Applications/ChatGPT.app/Contents/Resources/codex`.
4. `~/.codex/plugins/.plugin-appserver/codex`.
5. `/opt/homebrew/bin/codex` and `/usr/local/bin/codex`.

On Windows it checks `CODEX_BINARY`, `CODEX_CLI_PATH`, `PATH`, official standalone installs, relocated Codex Desktop runtimes, the Microsoft Store app cache, the npm global command directory, Windows app aliases, and common ChatGPT installation directories. Native Windows Codex and ChatGPT share `%USERPROFILE%\.codex`; set `CODEX_HOME` when you intentionally keep sessions elsewhere. macOS also honors `CODEX_HOME` and reads its `sessions` directory, so both native apps support the same explicit Codex path and data-home overrides.

## Web component preview

The repository also includes the embeddable `usage-widget.js`. To preview it locally:

```bash
python3 -m http.server 4173
```

Then open `http://localhost:4173`.

A native host can implement `window.codexMeterBridge` (`getUsage`, `consumeReset`, `resize`, and `quit`). Reset results are returned through `window.codexResetResult(payload)`. The legacy `window.codexUsageBridge.getUsage()` hook remains supported. A web host can provide a matching `GET /api/usage`, or push data directly:

```js
window.updateCodexUsage(payload);
window.recordCodexTurn({ inputTokens: 1200, outputTokens: 480 });
```

Hosts can optionally include `today.tokenSource`, `today.conversations`, and `history.dailyTokens` in the usage payload to populate the detail dialogs. Conversation entries may include `contextWindowId` and `threadName`; the widget falls back to `threadId` and then `turnId` when grouping older payloads, and to the first prompt when a task name is unavailable. Older payloads remain supported and show an empty detail state when these fields are absent.

## Privacy

Codex Meter has no analytics or separate backend, does not store account credentials, and does not upload local session content. See [PRIVACY.md](PRIVACY.md).

## License

[MIT](LICENSE)

Codex Meter is an unofficial community project and is not affiliated with or endorsed by OpenAI. Codex, ChatGPT, and OpenAI are trademarks of their respective owners.
