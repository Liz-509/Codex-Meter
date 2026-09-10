# Privacy

Codex Meter runs locally on the Mac.

- It starts the locally installed Codex App Server to read the signed-in account's rate-limit information.
- It scans local `~/.codex/sessions` event logs to total today's token counters and question count.
- It does not upload session content, store account credentials, add analytics, or operate a separate server.
- It does not modify the Codex or ChatGPT application.

The Codex App Server may communicate with OpenAI using the account already signed in through Codex. This project does not receive or proxy that traffic.
