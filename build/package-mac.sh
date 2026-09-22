#!/usr/bin/env bash
# Builds SavviedMatrix.app, a self-contained macOS bundle that needs no .NET installed.
#
#   ./build/package-mac.sh [arm64|x64]        (default: this machine's architecture)
#
# The result is dist/SavviedMatrix.app. Double-click it, or add it to Login Items to
# start the kiosk with the machine.
set -euo pipefail

cd "$(dirname "$0")/.."

ARCH="${1:-$( [ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64 )}"
RID="osx-${ARCH}"
APP="dist/SavviedMatrix.app"
STAGE="dist/.stage-${RID}"

# The SDK is often installed per-user rather than system-wide; prefer that copy.
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

echo "Publishing for ${RID}..."
rm -rf "$STAGE" "$APP"

dotnet publish src/SavviedMatrix \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishReadyToRun=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -o "$STAGE"

echo "Assembling ${APP}..."
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# Everything published goes next to the executable inside the bundle. Runtime state
# (token, cache, log) is written to ~/Library/Application Support/SavviedMatrix instead,
# so the bundle itself stays read-only.
cp -R "$STAGE"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/SavviedMatrix"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>                 <string>SavviedMatrix</string>
  <key>CFBundleDisplayName</key>          <string>SavviedMatrix</string>
  <key>CFBundleIdentifier</key>           <string>com.savvied.matrix</string>
  <key>CFBundleVersion</key>              <string>1.0</string>
  <key>CFBundleShortVersionString</key>   <string>1.0</string>
  <key>CFBundlePackageType</key>          <string>APPL</string>
  <key>CFBundleExecutable</key>           <string>SavviedMatrix</string>
  <key>CFBundleIconFile</key>             <string>AppIcon</string>
  <key>LSMinimumSystemVersion</key>       <string>12.0</string>

  <!-- A picture frame has no business in the Dock or the menu bar, but it does need to
       come to the front and take key events, so it is a normal app rather than an agent. -->
  <key>LSUIElement</key>                  <false/>
  <key>NSHighResolutionCapable</key>      <true/>

  <!-- The window covers the screen itself; letting the system hide the menu bar and Dock
       for it is what stops either sliding over the picture. -->
  <key>LSApplicationCategoryType</key>    <string>public.app-category.entertainment</string>
</dict>
</plist>
PLIST

rm -rf "$STAGE"

# An unsigned bundle copied from elsewhere is quarantined; an ad-hoc signature plus
# clearing the flag is enough for a machine you control and avoids the Gatekeeper prompt.
codesign --force --deep --sign - "$APP" 2>/dev/null \
  && echo "Ad-hoc signed." \
  || echo "Could not ad-hoc sign; Gatekeeper may prompt on first launch."

xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

echo
echo "Built ${APP}"
du -sh "$APP" | sed 's/^/  size: /'
echo "  run:  open ${APP}"
echo "  logs: ~/Library/Application Support/SavviedMatrix/SavviedMatrix.log"
