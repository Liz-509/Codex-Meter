#!/bin/zsh
set -euo pipefail

project_dir="${0:A:h:h}"
test_binary="${TMPDIR:-/tmp}/codex-meter-analytics-tests"
module_cache="${TMPDIR:-/tmp}/codex-meter-test-module-cache"
sdk_path="${CODEX_USAGE_SDK:-/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk}"

mkdir -p "$module_cache"

swiftc \
  -D CODEX_METER_TESTING \
  -sdk "$sdk_path" \
  -module-cache-path "$module_cache" \
  "$project_dir/native/RemoteSessionCollector.swift" \
  "$project_dir/native/CodexUsageService.swift" \
  "$project_dir/native/CodexAnalytics.swift" \
  "$project_dir/native/CodexAnalyticsTests.swift" \
  -o "$test_binary"

"$test_binary"
node --check "$project_dir/usage-widget.js"
node "$project_dir/scripts/test-widget-state.js"
