#!/bin/sh
set -eu

cd "$(dirname "$0")"
if [ "$(uname -s)" != Darwin ]; then
    printf 'Run this script on macOS.\n' >&2
    exit 1
fi

app="$(pwd)/dist/CAUC Campus Network.app"
mkdir -p "$app/Contents/MacOS"
cp Info.plist "$app/Contents/Info.plist"
CGO_ENABLED=0 go build -buildvcs=false -trimpath -o "$app/Contents/MacOS/CAUCCampusNetwork" .
/usr/bin/codesign --force --sign - "$app"
printf '%s\n' "$app"
