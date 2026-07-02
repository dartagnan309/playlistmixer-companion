#!/bin/sh
# PlaylistMixer Companion — macOS uninstaller. Stops + unregisters the LaunchAgent and removes the
# installed files. Recordings under ~/Movies/PlaylistMixer Recordings are left in place.
set -eu

LABEL="com.playlistmixer.companion"
PREFIX="$HOME/Library/Application Support/PlaylistMixer Companion"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

echo "Removing PlaylistMixer Companion..."
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
rm -f "$PLIST"
rm -rf "$PREFIX"
echo "OK: Removed. (Recordings in ~/Movies/PlaylistMixer Recordings were kept.)"
