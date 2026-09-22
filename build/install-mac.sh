#!/usr/bin/env bash
# Installs SavviedMatrix as a standalone kiosk on this Mac.
#
#   ./build/install-mac.sh             install (or re-install over a running copy)
#   ./build/install-mac.sh uninstall   remove the app and the autostart agent
#
# Needs no sudo and no .NET: the bundle carries its own runtime. Everything it touches is
# /Applications/SavviedMatrix.app and one file in ~/Library/LaunchAgents.
set -euo pipefail

cd "$(dirname "$0")/.."

APP_NAME="SavviedMatrix.app"
INSTALLED="/Applications/${APP_NAME}"
LABEL="com.savvied.matrix"
AGENT="$HOME/Library/LaunchAgents/${LABEL}.plist"
DATA="$HOME/Library/Application Support/SavviedMatrix"
DOMAIN="gui/$(id -u)"

unload_agent() {
  launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
}

if [ "${1:-install}" = "uninstall" ]; then
  echo "Removing the autostart agent..."
  unload_agent
  rm -f "$AGENT"

  echo "Stopping and removing the app..."
  pkill -f "${INSTALLED}/Contents/MacOS/SavviedMatrix" 2>/dev/null || true
  rm -rf "$INSTALLED"

  echo
  echo "Removed. Your settings and Dropbox token are untouched, in:"
  echo "  $DATA"
  echo "Delete that folder too if you want it gone completely."
  exit 0
fi

if [ ! -d "dist/${APP_NAME}" ]; then
  echo "dist/${APP_NAME} is missing. Run ./build/package-mac.sh first." >&2
  exit 1
fi

# Stop anything already running, including a previous agent, or the copy fails on a
# busy executable and launchd keeps restarting the old one mid-install.
echo "Stopping any running copy..."
unload_agent
pkill -f "${INSTALLED}/Contents/MacOS/SavviedMatrix" 2>/dev/null || true
sleep 1

echo "Installing to ${INSTALLED}..."
rm -rf "$INSTALLED"
cp -R "dist/${APP_NAME}" "$INSTALLED"
xattr -dr com.apple.quarantine "$INSTALLED" 2>/dev/null || true

mkdir -p "$DATA" "$HOME/Library/LaunchAgents"

echo "Writing the autostart agent..."
cat > "$AGENT" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${LABEL}</string>

  <!-- The executable inside the bundle, not "open". launchd has to supervise the real
       process; "open" would exit immediately and look like a crash every time. -->
  <key>ProgramArguments</key>
  <array>
    <string>${INSTALLED}/Contents/MacOS/SavviedMatrix</string>
  </array>

  <key>RunAtLoad</key>
  <true/>

  <!-- Restart it if it dies, but only if it died badly. Any key or click quits the app
       deliberately and returns success, so this leaves it closed when someone means to
       close it, and you are not locked out of the machine. -->
  <key>KeepAlive</key>
  <dict>
    <key>SuccessfulExit</key>
    <false/>
  </dict>

  <!-- Never respawn faster than this, so a persistent failure cannot become a spin. -->
  <key>ThrottleInterval</key>
  <integer>10</integer>

  <key>ProcessType</key>
  <string>Interactive</string>

  <key>StandardOutPath</key>
  <string>${DATA}/launchd.out.log</string>
  <key>StandardErrorPath</key>
  <string>${DATA}/launchd.err.log</string>
</dict>
</plist>
PLIST

echo "Starting..."
launchctl bootstrap "$DOMAIN" "$AGENT"

echo
echo "Installed."
echo "  app:      ${INSTALLED}"
echo "  autostart: ${AGENT}  (starts at login, restarts only on a crash)"
echo "  settings:  ${DATA}/config.json"
echo "  log:       ${DATA}/SavviedMatrix.log"
echo
echo "Any key or click quits it and it stays quit. To start it again:"
echo "  launchctl kickstart ${DOMAIN}/${LABEL}"
echo "To remove everything:  ./build/install-mac.sh uninstall"
