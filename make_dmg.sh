#!/bin/bash
# Makes HMLOG-<arch>.dmg on a Mac from the app bundles that publish.ps1 builds on Windows.
#
#   1. copy publish/HMLOG-osx-arm64.app (and/or HMLOG-osx-x64.app) and this script to the Mac
#      (copy the folder as it is; a zip made on Windows loses the executable bit, the script sets it again)
#   2. ./make_dmg.sh            both architectures found next to the script
#      ./make_dmg.sh arm64      only HMLOG-osx-arm64.app   (Apple Silicon)
#      ./make_dmg.sh x64        only HMLOG-osx-x64.app     (Intel)
#   3. result: HMLOG-arm64.dmg / HMLOG-x64.dmg next to the script (the disk shows HMLOG.app and an Applications shortcut)
#
# The app is signed ad hoc (codesign -s -): Apple Silicon refuses to start unsigned code. It is not notarized, so the
# first start of a downloaded copy needs right-click > Open (or: xattr -cr /Applications/HMLOG.app).
set -e
cd "$(dirname "$0")"

make_one() {
    local arch="$1"
    local src="HMLOG-osx-$arch.app"
    local dmg="HMLOG-$arch.dmg"
    if [ ! -d "$src" ]; then
        echo "skip $arch: $src not found"
        return 0
    fi
    local stage
    stage="$(mktemp -d)"
    cp -R "$src" "$stage/HMLOG.app"
    chmod +x "$stage/HMLOG.app/Contents/MacOS/HMLOG.App"
    xattr -cr "$stage/HMLOG.app" 2>/dev/null || true
    codesign --force --deep --sign - "$stage/HMLOG.app"
    ln -s /Applications "$stage/Applications"
    rm -f "$dmg"
    hdiutil create -volname "HMLOG" -srcfolder "$stage" -ov -format UDZO "$dmg"
    rm -rf "$stage"
    echo "made $dmg"
}

case "${1:-both}" in
    arm64) make_one arm64 ;;
    x64)   make_one x64 ;;
    both)  make_one arm64; make_one x64 ;;
    *)     echo "usage: $0 [arm64|x64|both]"; exit 1 ;;
esac
