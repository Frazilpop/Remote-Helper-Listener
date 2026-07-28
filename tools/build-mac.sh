#!/usr/bin/env bash
#
# Build "Remote Helper.app" for macOS — a menu bar app, one bundle per
# architecture — and wrap each in a drag-to-Applications DMG. Signing is
# automatic when a Developer ID Application certificate is in the keychain;
# without one the app is ad-hoc signed (fine for local use, Gatekeeper will
# balk on other machines).
#
#   tools/build-mac.sh              # -> dist/mac/RemoteHelperListener-mac-{arm64,x64}.dmg
#   tools/build-mac.sh --notarize   # ...also notarize + staple app and DMG
#
# Notarization needs a one-time credential setup on this machine:
#   xcrun notarytool store-credentials remote-helper \
#     --apple-id <apple-id> --team-id 686JLTG778 --password <app-specific-password>
# (Override the profile name with NOTARY_PROFILE=<name>.)
#
set -euo pipefail
cd "$(dirname "$0")/.."

NOTARIZE=0
[ "${1:-}" = "--notarize" ] && NOTARIZE=1
PROFILE="${NOTARY_PROFILE:-remote-helper}"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' listener/RemoteHelper.Listener.csproj)"
SIGN_ID="$(security find-identity -v -p codesigning 2>/dev/null \
    | sed -n 's/.*"\(Developer ID Application: [^"]*\)".*/\1/p' | head -1)"

if [ "$NOTARIZE" = 1 ]; then
    [ -n "$SIGN_ID" ] || { echo "ABORT: --notarize needs a Developer ID Application certificate." >&2; exit 1; }
    xcrun notarytool history --keychain-profile "$PROFILE" >/dev/null 2>&1 || {
        echo "ABORT: no notarytool profile '$PROFILE' — run the store-credentials command in this script's header." >&2
        exit 1; }
fi
echo "==> v$VERSION, signing as: ${SIGN_ID:-ad-hoc (no Developer ID certificate found)}"

for ARCH in arm64 x64; do
    OUT="dist/mac/$ARCH"
    APP="$OUT/Remote Helper.app"
    rm -rf "$OUT"

    # Self-contained but NOT single-file: every Mach-O stays a separate,
    # individually signable file, which is what notarization wants (the
    # single-file self-extractor trips Gatekeeper on the extracted bits).
    dotnet publish listener -c Release -f net7.0 -r "osx-$ARCH" --self-contained \
        -o "$OUT/publish"

    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
    rm -f "$APP/Contents/MacOS/"*.pdb
    cp listener/mac/AppIcon.icns "$APP/Contents/Resources/"

    cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Remote Helper</string>
    <key>CFBundleDisplayName</key><string>Remote Helper</string>
    <key>CFBundleIdentifier</key><string>com.frazilpop.RemoteHelperListener</string>
    <key>CFBundleExecutable</key><string>RemoteHelperListener</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSLocalNetworkUsageDescription</key>
    <string>Remote Helper listens on your Wi-Fi so your iPhone or iPad can send keystrokes to this Mac.</string>
    <key>NSHumanReadableCopyright</key><string>© Fraser Mackenzie · MIT license</string>
</dict>
</plist>
EOF

    if [ -n "$SIGN_ID" ]; then
        # Inside-out: every file in MacOS/ first (codesign counts them all —
        # managed .dlls and even the .json configs — as nested code), then
        # the main executable with entitlements, then the bundle seal.
        find "$APP/Contents/MacOS" -type f ! -name RemoteHelperListener -exec \
            codesign --force --timestamp --options runtime --sign "$SIGN_ID" {} +
        codesign --force --timestamp --options runtime \
            --entitlements listener/mac/entitlements.plist \
            --sign "$SIGN_ID" "$APP/Contents/MacOS/RemoteHelperListener"
        codesign --force --timestamp --options runtime \
            --entitlements listener/mac/entitlements.plist --sign "$SIGN_ID" "$APP"
        codesign --verify --strict --deep "$APP"
    else
        codesign --force --deep --sign - "$APP"
    fi

    if [ "$NOTARIZE" = 1 ]; then
        # Notarize the app first so it can be stapled BEFORE it's frozen
        # into the DMG — then the DMG gets its own ticket too, so both the
        # disk image and a copied-out app pass Gatekeeper offline.
        ditto -c -k --keepParent "$APP" "$OUT/notarize-app.zip"
        xcrun notarytool submit "$OUT/notarize-app.zip" --keychain-profile "$PROFILE" --wait
        xcrun stapler staple "$APP"
        rm "$OUT/notarize-app.zip"
    fi

    STAGE="$OUT/dmg"
    mkdir -p "$STAGE"
    cp -R "$APP" "$STAGE/"
    ln -s /Applications "$STAGE/Applications"
    DMG="dist/mac/RemoteHelperListener-mac-$ARCH.dmg"
    rm -f "$DMG"
    hdiutil create -volname "Remote Helper" -srcfolder "$STAGE" -format UDZO -quiet "$DMG"
    if [ -n "$SIGN_ID" ]; then
        codesign --force --timestamp --sign "$SIGN_ID" "$DMG"
    fi

    if [ "$NOTARIZE" = 1 ]; then
        xcrun notarytool submit "$DMG" --keychain-profile "$PROFILE" --wait
        xcrun stapler staple "$DMG"
    fi

    echo "==> $DMG"
done
