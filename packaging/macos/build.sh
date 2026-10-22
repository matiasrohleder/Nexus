#!/usr/bin/env bash
# Builds artifacts/Nexus-<version>-macos-<arch>.dmg (drag Nexus.app to Applications).
# Usage: packaging/macos/build.sh [osx-arm64|osx-x64]       — must run on macOS.
#
# Signing: ad-hoc by default (runs after the user approves it once in Privacy & Security).
# If these environment variables are set, the app is signed with a Developer ID and notarized,
# so it opens with no warning at all:
#   MACOS_SIGN_IDENTITY  "Developer ID Application: Name (TEAMID)"  (certificate in the keychain)
#   APPLE_ID, APPLE_TEAM_ID, APPLE_APP_PASSWORD                     (notarytool credentials)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RID="${1:-osx-arm64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Nexus.csproj")"
case "$RID" in
    osx-arm64) ARCH=arm64 ;;
    osx-x64)   ARCH=x64 ;;
    *) echo "unsupported RID: $RID" >&2; exit 1 ;;
esac
NAME="Nexus-$VERSION-macos-$ARCH"
OUT="$ROOT/artifacts"
WORK="$OUT/work/$RID"
rm -rf "$WORK"; mkdir -p "$WORK" "$OUT"

echo ">> publishing $RID ($VERSION)"
dotnet publish "$ROOT/Nexus.csproj" -c Release -r "$RID" --self-contained true \
    -p:UseAppHost=true -p:DebugType=none -p:DebugSymbols=false -o "$WORK/publish"

echo ">> assembling Nexus.app"
APP="$WORK/dmg/Nexus.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -a "$WORK/publish/." "$APP/Contents/MacOS/"
cp "$ROOT/packaging/icons/nexus.icns" "$APP/Contents/Resources/nexus.icns"
sed "s/__VERSION__/$VERSION/g" "$ROOT/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
chmod +x "$APP/Contents/MacOS/Nexus"
xattr -cr "$APP"

if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
    echo ">> signing with Developer ID"
    # Sign nested native code first, then the bundle, with the hardened runtime.
    find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -u+x \) -print0 |
        xargs -0 codesign --force --timestamp --options runtime \
            --entitlements "$ROOT/packaging/macos/entitlements.plist" --sign "$MACOS_SIGN_IDENTITY"
    codesign --force --timestamp --options runtime \
        --entitlements "$ROOT/packaging/macos/entitlements.plist" --sign "$MACOS_SIGN_IDENTITY" "$APP"
else
    echo ">> ad-hoc signing"
    codesign --force --deep --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"

echo ">> building .dmg"
ln -s /Applications "$WORK/dmg/Applications"
rm -f "$OUT/$NAME.dmg"
hdiutil create -volname "Nexus $VERSION" -srcfolder "$WORK/dmg" -fs HFS+ -format UDZO -ov "$OUT/$NAME.dmg" >/dev/null

if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
    codesign --force --timestamp --sign "$MACOS_SIGN_IDENTITY" "$OUT/$NAME.dmg"
    if [ -n "${APPLE_ID:-}" ]; then
        echo ">> notarizing"
        xcrun notarytool submit "$OUT/$NAME.dmg" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" \
            --password "$APPLE_APP_PASSWORD" --wait
        xcrun stapler staple "$OUT/$NAME.dmg"
    fi
fi

echo ">> done: $OUT/$NAME.dmg"
