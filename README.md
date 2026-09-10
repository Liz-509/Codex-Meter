# Codex Meter

A lightweight native usage widget for Codex on macOS and Windows. It starts as a draggable icon, expands on hover, and collapses when the pointer leaves.

![Codex Meter in light mode](assets/screenshots/codex-meter-light.jpg)

## Download

- [macOS Apple Silicon — Codex Meter v1.1.1](https://github.com/Liz-509/Codex-Meter/releases/download/v1.1.1/Codex-Meter-macOS-arm64-v1.1.1.zip)
- [Windows 10/11 x64 — Codex Meter v1.1.1](https://github.com/Liz-509/Codex-Meter/releases/download/v1.1.1/Codex-Meter-Windows-x64-v1.1.1.zip)

Unzip the archive. On macOS, move `Codex Meter.app` to your Applications folder and open it. On Windows, run `Codex Meter.exe` directly; installation and administrator access are not required. Intel Mac users can build from source.

Because the app is not notarized, macOS may ask you to confirm the first launch. Control-click the app, choose **Open**, then confirm **Open**.

## Features

- Shows the five-hour limit, weekly limit, reset times, and available reset credits.
- Totals today's tokens and questions in the device's local time zone.
- Supports automatic, light, and dark themes.
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

The self-contained x64 app is written to `build\windows\win-x64`, and the portable archive is written to `outputs\Codex-Meter-Windows-x64-v1.1.1.zip`. Extract the archive and run `Codex Meter.exe`; installation and administrator access are not required.

The Windows app stays on top on the current virtual desktop and includes a system tray menu. Windows does not expose a stable public equivalent of macOS “all Spaces,” and exclusive full-screen games may cover the widget.

## Data sources

- Limits, reset times, and reset credits come from the Codex App Server account endpoints.
- Today's tokens and question count are calculated locally from structured events in `~/.codex/sessions`.

The first Codex App Server launch may be slower. Codex Meter displays local statistics first and retries limit requests automatically when needed.

## Finding Codex

Codex Meter checks these locations in order:

1. The `CODEX_BINARY` environment variable.
2. `/Applications/ChatGPT.app/Contents/Resources/codex`.
3. `~/Applications/ChatGPT.app/Contents/Resources/codex`.
4. `~/.codex/plugins/.plugin-appserver/codex`.
5. `/opt/homebrew/bin/codex` and `/usr/local/bin/codex`.

On Windows it checks `CODEX_BINARY`, `PATH`, the npm global command directory, Windows app aliases, and common ChatGPT installation directories. Native Windows Codex and ChatGPT share `%USERPROFILE%\.codex`; set `CODEX_HOME` when you intentionally keep sessions elsewhere.

## Web component preview

The repository also includes the embeddable `usage-widget.js`. To preview it locally:

```bash
python3 -m http.server 4173
```

Then open `http://localhost:4173`.

A native host can implement `window.codexMeterBridge` (`getUsage`, `resize`, and `quit`). The legacy `window.codexUsageBridge.getUsage()` hook remains supported. A web host can provide a matching `GET /api/usage`, or push data directly:

```js
window.updateCodexUsage(payload);
window.recordCodexTurn({ inputTokens: 1200, outputTokens: 480 });
```

## Privacy

Codex Meter has no analytics or separate backend, does not store account credentials, and does not upload local session content. See [PRIVACY.md](PRIVACY.md).

## License

[MIT](LICENSE)

Codex Meter is an unofficial community project and is not affiliated with or endorsed by OpenAI. Codex, ChatGPT, and OpenAI are trademarks of their respective owners.
