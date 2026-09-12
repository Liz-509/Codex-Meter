#!/bin/zsh
set -euo pipefail

project_dir="${0:A:h:h}"
build_dir="$project_dir/build"
app_dir="$build_dir/Codex Meter.app"
contents_dir="$app_dir/Contents"
macos_dir="$contents_dir/MacOS"
resources_dir="$contents_dir/Resources"
module_cache="$build_dir/module-cache"
iconset_dir="$build_dir/AppIcon.iconset"
icon_generator="$build_dir/AppIconGenerator"
icns_builder="$build_dir/ICNSBuilder"
ico_builder="$build_dir/ICOBuilder"
windows_icon="$project_dir/windows/CodexMeter.Windows/Assets/AppIcon.ico"

host_arch="${CODEX_USAGE_ARCH:-$(uname -m)}"
deployment_target="${MACOSX_DEPLOYMENT_TARGET:-13.0}"

case "$host_arch" in
  arm64|x86_64) ;;
  *)
    echo "Unsupported Mac architecture: $host_arch" >&2
    exit 1
    ;;
esac

if [[ -n "${CODEX_USAGE_SDK:-}" ]]; then
  sdk_path="$CODEX_USAGE_SDK"
elif [[ -d "/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk" ]]; then
  sdk_path="/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk"
else
  sdk_path="$(xcrun --sdk macosx --show-sdk-path)"
fi

swiftc_bin="${SWIFTC:-$(xcrun --find swiftc 2>/dev/null || command -v swiftc)}"
target="$host_arch-apple-macos$deployment_target"

mkdir -p "$macos_dir" "$resources_dir" "$module_cache" "$iconset_dir" "${windows_icon:h}"

"$swiftc_bin" \
  "$project_dir/native/AppIconGenerator.swift" \
  -o "$icon_generator" \
  -framework AppKit \
  -sdk "$sdk_path" \
  -target "$target" \
  -parse-as-library \
  -module-cache-path "$module_cache"

render_icon() {
  local pixels="$1"
  local output_name="$2"
  "$icon_generator" "$pixels" "$iconset_dir/$output_name"
}

render_icon 16 icon_16x16.png
render_icon 32 icon_16x16@2x.png
render_icon 32 icon_32x32.png
render_icon 64 icon_32x32@2x.png
render_icon 128 icon_128x128.png
render_icon 256 icon_128x128@2x.png
render_icon 256 icon_256x256.png
render_icon 512 icon_256x256@2x.png
render_icon 512 icon_512x512.png
render_icon 1024 icon_512x512@2x.png

"$swiftc_bin" \
  "$project_dir/native/ICNSBuilder.swift" \
  -o "$icns_builder" \
  -sdk "$sdk_path" \
  -target "$target" \
  -parse-as-library \
  -module-cache-path "$module_cache"
"$icns_builder" "$iconset_dir" "$resources_dir/AppIcon.icns"

"$swiftc_bin" \
  "$project_dir/native/ICOBuilder.swift" \
  -o "$ico_builder" \
  -sdk "$sdk_path" \
  -target "$target" \
  -parse-as-library \
  -module-cache-path "$module_cache"
"$ico_builder" "$iconset_dir" "$windows_icon"

"$swiftc_bin" \
  "$project_dir/native/CompanionApp.swift" \
  "$project_dir/native/CodexUsageService.swift" \
  -o "$macos_dir/CodexUsageCompanion" \
  -framework AppKit \
  -framework ServiceManagement \
  -framework WebKit \
  -sdk "$sdk_path" \
  -target "$target" \
  -module-cache-path "$module_cache"

cp "$project_dir/native/Info.plist" "$contents_dir/Info.plist"
cp "$project_dir/companion.html" "$resources_dir/companion.html"
cp "$project_dir/usage-widget.js" "$resources_dir/usage-widget.js"

codesign --force --deep --sign - "$app_dir"

echo "$app_dir"
