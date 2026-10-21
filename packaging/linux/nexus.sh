#!/bin/sh
# Nexus launcher. Self-contained .NET apps need the system ICU library for culture data; on the
# rare minimal distro without it, fall back to invariant globalization so Nexus always starts.
APP_DIR="__APP_DIR__"
if [ -z "${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-}" ]; then
    icu=""
    for f in /usr/lib/libicuuc.so.* /usr/lib64/libicuuc.so.* /usr/lib/*/libicuuc.so.* /lib/*/libicuuc.so.*; do
        if [ -e "$f" ]; then icu=1; break; fi
    done
    [ -n "$icu" ] || export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi
exec "$APP_DIR/Nexus" "$@"
