#!/bin/zsh
set -euo pipefail

project_dir="${0:A:h:h}"
app_dir="$project_dir/build/Codex Meter.app"
output_dir="$project_dir/outputs"
staging_dir="$project_dir/build/macos-installer"

version="${1:-${RELEASE_VERSION:-}}"
if [[ -z "$version" ]]; then
  version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$project_dir/native/Info.plist")"
fi

if [[ ! "$version" =~ '^[0-9A-Za-z][0-9A-Za-z._-]*$' ]]; then
  echo "Invalid version: $version" >&2
  exit 1
fi

arch="${CODEX_USAGE_ARCH:-$(uname -m)}"
case "$arch" in
  arm64|x86_64) ;;
  *)
    echo "Unsupported Mac architecture: $arch" >&2
    exit 1
    ;;
esac

installer="$output_dir/Codex-Meter-macOS-${arch}-v${version}.dmg"

"$project_dir/scripts/build-native.sh"

if [[ ! -x "$app_dir/Contents/MacOS/CodexUsageCompanion" ]]; then
  echo "macOS build did not produce a runnable Codex Meter.app" >&2
  exit 1
fi

app_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app_dir/Contents/Info.plist")"
if [[ "$app_version" != "$version" ]]; then
  echo "Installer version $version does not match app version $app_version" >&2
  exit 1
fi

rm -rf "$staging_dir"
mkdir -p "$staging_dir" "$output_dir"
ditto "$app_dir" "$staging_dir/Codex Meter.app"
ln -s /Applications "$staging_dir/Applications"

rm -f "$installer"
hdiutil create \
  -volname "Codex Meter" \
  -srcfolder "$staging_dir" \
  -format UDZO \
  -imagekey zlib-level=9 \
  -ov \
  "$installer"
hdiutil verify "$installer"

echo "$installer"
