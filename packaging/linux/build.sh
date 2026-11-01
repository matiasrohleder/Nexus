#!/usr/bin/env bash
# Builds the Linux installers into artifacts/:
#   Nexus-<version>-linux-<arch>.deb      Debian, Ubuntu, Mint, Pop!_OS…
#   Nexus-<version>-linux-<arch>.rpm      Fedora, RHEL, openSUSE…   (needs rpmbuild)
#   Nexus-<version>-linux-<arch>.tar.gz   any distro (./install.sh, no root)
# Usage: packaging/linux/build.sh [linux-x64|linux-arm64]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RID="${1:-linux-x64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Nexus.csproj")"
case "$RID" in
    linux-x64)   DEB_ARCH=amd64; RPM_ARCH=x86_64;  ARCH=x64 ;;
    linux-arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64; ARCH=arm64 ;;
    *) echo "unsupported RID: $RID" >&2; exit 1 ;;
esac
NAME="Nexus-$VERSION-linux-$ARCH"
OUT="$ROOT/artifacts"
WORK="$OUT/work/$RID"
LNX="$ROOT/packaging/linux"
rm -rf "$WORK"; mkdir -p "$WORK" "$OUT"

echo ">> publishing $RID ($VERSION)"
dotnet publish "$ROOT/Nexus.csproj" -c Release -r "$RID" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -o "$WORK/app"

# ---- filesystem layout shared by .deb and .rpm ----
STAGE="$WORK/root"
install -d "$STAGE/opt/nexus" "$STAGE/usr/bin" "$STAGE/usr/share/applications"
cp -a "$WORK/app/." "$STAGE/opt/nexus/"
chmod 755 "$STAGE/opt/nexus/Nexus"
sed "s|__APP_DIR__|/opt/nexus|" "$LNX/nexus.sh" > "$STAGE/usr/bin/nexus"
chmod 755 "$STAGE/usr/bin/nexus"
install -m 644 "$LNX/nexus.desktop" "$STAGE/usr/share/applications/nexus.desktop"
for d in "$ROOT"/packaging/icons/hicolor/*x*; do
    size="$(basename "$d")"
    install -D -m 644 "$d/nexus.png" "$STAGE/usr/share/icons/hicolor/$size/apps/nexus.png"
done
find "$STAGE" -type d -exec chmod 755 {} +

DESCRIPTION="Lightweight, cross-platform database administrator"
LONG="Nexus is a fast desktop administrator for PostgreSQL, MySQL, MariaDB, SQL Server, Oracle, SQLite, DuckDB, ClickHouse, MongoDB and Redis. Passwords are kept in the system keyring."
MAINTAINER="Matías D. Rohleder <matiasrohleder@users.noreply.github.com>"
HOMEPAGE="https://github.com/matiasrohleder/Nexus"

# ---- .deb ----
echo ">> building .deb"
DEB="$WORK/deb"
cp -a "$STAGE" "$DEB"
install -d "$DEB/DEBIAN"
SIZE_KB="$(du -sk "$STAGE" | cut -f1)"
cat > "$DEB/DEBIAN/control" <<CONTROL
Package: nexus-db
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: $MAINTAINER
Installed-Size: $SIZE_KB
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libxi6, libxrandr2, libxcursor1, libssl3 | libssl3t64 | libssl1.1
Recommends: libsecret-1-0, gnome-keyring | kwalletmanager | keepassxc
Suggests: postgresql-client, default-mysql-client | mariadb-client, mongodb-database-tools, redis-tools
Section: database
Priority: optional
Homepage: $HOMEPAGE
Description: $DESCRIPTION
 $LONG
CONTROL
cat > "$DEB/DEBIAN/postinst" <<'SCRIPT'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
exit 0
SCRIPT
cp "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
chmod 755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
dpkg-deb --root-owner-group --build -Zxz "$DEB" "$OUT/$NAME.deb" >/dev/null

# ---- .rpm ----
if command -v rpmbuild >/dev/null 2>&1; then
    echo ">> building .rpm"
    RPMTOP="$WORK/rpmbuild"
    mkdir -p "$RPMTOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
    LIB64="()(64bit)"
    cat > "$RPMTOP/SPECS/nexus.spec" <<SPEC
%global debug_package %{nil}
%global __os_install_post %{nil}
%define _build_id_links none
Name:           nexus-db
Version:        $VERSION
Release:        1
Summary:        $DESCRIPTION
License:        Proprietary
URL:            $HOMEPAGE
BuildArch:      $RPM_ARCH
AutoReqProv:    no
Requires:       libX11.so.6$LIB64, libICE.so.6$LIB64, libSM.so.6$LIB64, libXi.so.6$LIB64
Requires:       libXrandr.so.2$LIB64, libXcursor.so.1$LIB64, libfontconfig.so.1$LIB64
Requires:       libz.so.1$LIB64, libstdc++.so.6$LIB64, libssl.so.3$LIB64
Recommends:     libsecret-1.so.0$LIB64
Suggests:       postgresql, mariadb, mongodb-database-tools, redis

%description
$LONG

%install
cp -a "$STAGE/." %{buildroot}/

%post
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :
gtk-update-icon-cache -q -t /usr/share/icons/hicolor >/dev/null 2>&1 || :

%postun
update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || :

%files
/opt/nexus
/usr/bin/nexus
/usr/share/applications/nexus.desktop
/usr/share/icons/hicolor/*/apps/nexus.png
SPEC
    rpmbuild --quiet --define "_topdir $RPMTOP" -bb "$RPMTOP/SPECS/nexus.spec"
    cp "$RPMTOP/RPMS/$RPM_ARCH/"*.rpm "$OUT/$NAME.rpm"
else
    echo ">> rpmbuild not found — skipping .rpm"
fi

# ---- .tar.gz (per-user install, any distro) ----
echo ">> building .tar.gz"
TAR="$WORK/tar/$NAME"
mkdir -p "$TAR/icons"
cp -a "$WORK/app" "$TAR/app"
cp "$LNX/install.sh" "$LNX/nexus.sh" "$LNX/nexus.desktop" "$TAR/"
chmod 755 "$TAR/install.sh"
cp -a "$ROOT"/packaging/icons/hicolor/. "$TAR/icons/"
tar -C "$WORK/tar" -czf "$OUT/$NAME.tar.gz" "$NAME"

echo ">> done:"; ls -lh "$OUT"/"$NAME".*
