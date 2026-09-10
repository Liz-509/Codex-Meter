#!/bin/zsh
set -euo pipefail

project_dir="${0:A:h}"
destination_dir="${HOME}/Applications"
destination_app="$destination_dir/Codex Meter.app"

"$project_dir/scripts/build-native.sh"
mkdir -p "$destination_dir"
ditto "$project_dir/build/Codex Meter.app" "$destination_app"

echo
echo "Installed: $destination_app"
echo "You can now open Codex Meter from your Applications folder."
open -R "$destination_app"
