#!/bin/zsh
set -euo pipefail

project_dir="${0:A:h:h}"
test_binary="${TMPDIR:-/tmp}/codex-meter-analytics-tests"

swiftc \
  -D CODEX_METER_TESTING \
  "$project_dir/native/CodexUsageService.swift" \
  "$project_dir/native/CodexAnalytics.swift" \
  "$project_dir/native/CodexAnalyticsTests.swift" \
  -o "$test_binary"

"$test_binary"
node --check "$project_dir/usage-widget.js"
node "$project_dir/scripts/test-widget-state.js"
