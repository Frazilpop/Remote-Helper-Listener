#!/usr/bin/env bash
#
# Build the macOS listener as self-contained single-file binaries — the
# machine that runs one needs no .NET installed. One binary per
# architecture, each zipped with ditto so the executable bit survives the
# release-download-unzip round trip.
#
#   tools/build-mac.sh    # -> dist/mac/RemoteHelperListener-mac-{arm64,x64}.zip
#
set -euo pipefail
cd "$(dirname "$0")/.."

for ARCH in arm64 x64; do
    OUT="dist/mac/$ARCH"
    dotnet publish listener -c Release -f net7.0 -r "osx-$ARCH" --self-contained \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -o "$OUT"
    ditto -c -k "$OUT/RemoteHelperListener" "dist/mac/RemoteHelperListener-mac-$ARCH.zip"
    echo "==> dist/mac/RemoteHelperListener-mac-$ARCH.zip"
done
