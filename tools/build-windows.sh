#!/usr/bin/env bash
# Build the self-contained Windows exe locally (no CI involved).
# Run from anywhere; output lands in dist/RemoteHelperListener.exe.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet publish listener -c Release -f net7.0-windows -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o dist

# Cross-building skips the SDK's GUI-subsystem marking (NETSDK1074), which
# would give the tray app a phantom terminal window. Patch it ourselves.
python3 tools/patch-gui-subsystem.py dist/RemoteHelperListener.exe

# The committed copy at the repo root is what syncs to the Windows PC.
cp dist/RemoteHelperListener.exe ./RemoteHelperListener.exe

echo
echo "Built and copied to repo root. Commit + push, then pull on the PC."
