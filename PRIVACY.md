# Privacy

Codex Meter runs locally on your device.

- It starts the locally installed Codex App Server to read the signed-in account's rate-limit information.
- It redeems an existing rate-limit reset credit only after the user opens the confirmation prompt and selects **Confirm**. It never redeems credits automatically and cannot purchase new credits or resets.
- It reads optional account-level daily token buckets from the locally installed Codex App Server to show up to 90 days of usage.
- It reads task names and recent-interaction ordering from the local Codex App Server thread list to label context groups and follow the current task.
- It scans local `~/.codex/sessions` event logs to calculate fallback daily token totals, project/task breakdowns, conversation turns, prompt previews, per-turn token counts, current context-window occupancy, and compaction counts.
- Context health is calculated locally from the current-window token count and model context limit already present in session events. For live updates, only the current session log tail is re-read about every two seconds; these measurements are not uploaded or stored in a separate database.
- Local prompt previews are rendered only inside Codex Meter's local WebView. They are not stored separately or sent to Codex Meter infrastructure.
- On macOS, it stores up to 14 days of quota percentages and reset timestamps in the user's Application Support folder to calculate trend forecasts. These snapshots contain no prompts, source code, file contents, or credentials.
- macOS notifications are disabled until the user explicitly enables them and grants system permission. Notification preferences and deduplication state are stored in local user defaults.
- Markdown and CSV reports are generated locally and written only to a location chosen by the user in the system save dialog.
- It does not upload session content, store account credentials, add analytics, or operate a separate server.
- It does not modify the Codex or ChatGPT application.

The Codex App Server may communicate with OpenAI using the account already signed in through Codex. This project does not receive or proxy that traffic.
