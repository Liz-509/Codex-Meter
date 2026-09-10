# Contributing

Issues and pull requests are welcome.

Before submitting a change:

1. Run `scripts/build-native.sh`.
2. Run `node --check usage-widget.js` if Node.js is available.
3. For Windows changes, run `dotnet test windows/CodexMeter.Windows.Tests/CodexMeter.Windows.Tests.csproj` and `scripts/build-windows.ps1` on Windows 10 22H2 or Windows 11.
4. Open the built app and verify compact, hover, drag, light, and dark states. Shared UI changes must be checked in both WKWebView and WebView2.
5. Keep account data local and never add telemetry or credential collection.

Use short-lived feature branches and merge them back into `main`; do not maintain permanent platform branches. Platform shells live in `native/` and `windows/`, while the HTML and JavaScript files at the repository root are shared.
