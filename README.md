# Codex Meter

A lightweight, native macOS usage widget for Codex. It starts as a draggable icon, expands on hover, and collapses when the pointer leaves.

## Features

- Shows the five-hour limit, weekly limit, reset times, and available reset credits.
- Totals today's tokens and questions in the Mac's local time zone.
- Supports automatic, light, and dark themes.
- Floats above other windows and appears across Spaces and full-screen apps.
- Remains interactive when another app is in front.
- Includes animated quota indicators and button micro-interactions.
- Runs locally without login items or Codex lifecycle hooks.

## Requirements

- macOS 13 or later.
- Apple Silicon or Intel Mac.
- ChatGPT/Codex desktop installed and signed in, or a local `codex` executable.
- Xcode Command Line Tools for source builds:

```bash
xcode-select --install
```

## Install from source

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

## Web component preview

The repository also includes the embeddable `usage-widget.js`. To preview it locally:

```bash
python3 -m http.server 4173
```

Then open `http://localhost:4173`.

A host can implement `window.codexUsageBridge.getUsage()`, provide a matching `GET /api/usage`, or push data directly:

```js
window.updateCodexUsage(payload);
window.recordCodexTurn({ inputTokens: 1200, outputTokens: 480 });
```

## Privacy

Codex Meter has no analytics or separate backend, does not store account credentials, and does not upload local session content. See [PRIVACY.md](PRIVACY.md).

## License

[MIT](LICENSE)

Codex Meter is an unofficial community project and is not affiliated with or endorsed by OpenAI. Codex, ChatGPT, and OpenAI are trademarks of their respective owners.
