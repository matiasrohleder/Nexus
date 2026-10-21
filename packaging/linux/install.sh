#!/bin/sh
# Installs Nexus for the current user (no root needed): any Linux distribution.
#   ./install.sh            install / upgrade
#   ./install.sh --uninstall
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_DIR="$PREFIX/nexus/app"
BIN_DIR="$HOME/.local/bin"

if [ "${1:-}" = "--uninstall" ]; then
    rm -rf "$PREFIX/nexus" "$BIN_DIR/nexus" "$PREFIX/applications/nexus.desktop"
    find "$PREFIX/icons/hicolor" -name nexus.png -path "*/apps/*" -delete 2>/dev/null || true
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$PREFIX/applications" || true
    echo "Nexus uninstalled. Your connections and settings remain in ~/.config/Nexus."
    exit 0
fi

rm -rf "$APP_DIR"
mkdir -p "$APP_DIR" "$BIN_DIR" "$PREFIX/applications"
cp -a "$HERE/app/." "$APP_DIR/"
chmod +x "$APP_DIR/Nexus"

sed "s|__APP_DIR__|$APP_DIR|" "$HERE/nexus.sh" > "$BIN_DIR/nexus"
chmod +x "$BIN_DIR/nexus"

for d in "$HERE"/icons/*x*; do
    size="$(basename "$d")"
    mkdir -p "$PREFIX/icons/hicolor/$size/apps"
    cp "$d/nexus.png" "$PREFIX/icons/hicolor/$size/apps/nexus.png"
done

# Absolute Exec path: ~/.local/bin is not on every desktop session's PATH.
sed "s|^Exec=nexus|Exec=$BIN_DIR/nexus|" "$HERE/nexus.desktop" > "$PREFIX/applications/nexus.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$PREFIX/applications" || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$PREFIX/icons/hicolor" || true

echo "Nexus installed. Launch it from your applications menu, or run: $BIN_DIR/nexus"
