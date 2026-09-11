# Privacy

Codex Meter runs locally on your device.

- It starts the locally installed Codex App Server to read the signed-in account's rate-limit information.
- It redeems an existing rate-limit reset credit only after the user opens the confirmation prompt and selects **Confirm**. It never redeems credits automatically and cannot purchase new credits or resets.
- It reads optional account-level daily token buckets from the locally installed Codex App Server to show the latest seven days of usage.
- It reads task names from the local Codex App Server thread list to label conversation context groups.
- It scans local `~/.codex/sessions` event logs to calculate fallback daily token totals and today's conversation turns, prompt previews, and per-turn token counts.
- Local prompt previews are rendered only inside Codex Meter's local WebView. They are not stored separately or sent to Codex Meter infrastructure.
- It does not upload session content, store account credentials, add analytics, or operate a separate server.
- It does not modify the Codex or ChatGPT application.

The Codex App Server may communicate with OpenAI using the account already signed in through Codex. This project does not receive or proxy that traffic.
