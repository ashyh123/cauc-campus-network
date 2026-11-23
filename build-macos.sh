#!/bin/sh
set -eu

cd "$(dirname "$0")"
if [ "$(uname -s)" != Darwin ]; then
    printf 'Run this script on macOS.\n' >&2
    exit 1
fi

swift build -c release --product CAUCCampusNetwork
bin_dir="$(swift build -c release --show-bin-path)"

app="$(pwd)/dist/CAUC Campus Network.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp Info.plist "$app/Contents/Info.plist"
cp AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
cp "$bin_dir/CAUCCampusNetwork" "$app/Contents/MacOS/CAUCCampusNetwork"
chmod +x "$app/Contents/MacOS/CAUCCampusNetwork"
/usr/bin/codesign --force --sign - "$app"
touch "$app"
printf '%s\n' "$app"
