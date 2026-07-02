#!/bin/sh
# PlaylistMixer Companion — macOS installer.
#
#   curl -fsSL https://github.com/dartagnan309/playlistmixer-companion/releases/latest/download/install.sh | sh
#
# The app's Companion page gives the exact one-liner it uses: it sets PLAYLISTMIXER_BASE_URL to the
# GitHub Releases base so the arch tarball is pulled from the same release.
#
# Downloads the self-contained service for this Mac's architecture, installs it under
# ~/Library/Application Support, and registers a per-user LaunchAgent so it starts at login and is
# kept alive. Because every artifact arrives via curl/tar (not a browser, not Homebrew Cask), nothing
# is quarantined — Gatekeeper never fires, so no notarization is required. (The binary IS ad-hoc
# codesigned at build time with JIT entitlements — required on Apple Silicon; see companion.entitlements.)
#
# Re-running this script upgrades in place (it bootout's the old agent before bootstrapping the new
# one). Uninstall with the companion's `uninstall.sh`, or manually: bootout the agent + rm the dirs.
set -eu

# ── Config ───────────────────────────────────────────────────────────────────────────────────────
BASE_URL="${PLAYLISTMIXER_BASE_URL:-https://github.com/dartagnan309/playlistmixer-companion/releases/latest/download}"
LABEL="com.playlistmixer.companion"
PREFIX="$HOME/Library/Application Support/PlaylistMixer Companion"
AGENTS="$HOME/Library/LaunchAgents"
PLIST="$AGENTS/$LABEL.plist"
SERVICE_BIN="PlaylistMixer.Companion.Service"

# ── Preconditions ──────────────────────────────────────────────────────────────────────────────
[ "$(uname -s)" = "Darwin" ] || { echo "This installer is for macOS only." >&2; exit 1; }

case "$(uname -m)" in
  arm64)  ARCH="arm64" ;;
  x86_64) ARCH="x64" ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

echo "Installing PlaylistMixer Companion (macOS $ARCH)..."

# ── 1. Tarball name ────────────────────────────────────────────────────────────────────────────
# Fixed (unversioned) name, like the Windows installer — the operator overwrites it each release and
# the in-app update banner (companion-version.json) is what nudges users to re-run this script.
TARBALL="companion-mac-$ARCH.tar.gz"

# ── 2. Stop any running instance, then unpack (curl => no quarantine) ──────────────────────────────
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true

mkdir -p "$PREFIX" "$AGENTS"
echo "Downloading ${TARBALL}..."
curl -fsSL "$BASE_URL/$TARBALL" | tar xz -C "$PREFIX"
chmod +x "$PREFIX/$SERVICE_BIN" 2>/dev/null || true
[ -f "$PREFIX/ffmpeg" ] && chmod +x "$PREFIX/ffmpeg" 2>/dev/null || true

# ── 2b. Ensure a valid JIT signature (Apple Silicon) ────────────────────────────────────────────────
# The service is a self-contained .NET app that JITs heavily at startup. On Apple Silicon, macOS
# enforces W^X and SIGKILLs a process that allocates executable memory unless it's signed (hardened
# runtime) with JIT entitlements (see companion.entitlements). build-mac.sh bakes that ad-hoc
# signature into the tarball at build time, so the normal case needs NOTHING here — and crucially no
# `codesign` (Xcode Command Line Tools) on the user's machine. We only fall back to signing in place
# if the binary somehow arrived unsigned (e.g. an old Windows-built tarball) AND codesign is present.
# Harmless to skip on Intel (W^X isn't enforced the same way there), so this is arm64-only.
ENTITLEMENTS="$PREFIX/companion.entitlements"
if [ "$ARCH" = "arm64" ] && command -v codesign >/dev/null 2>&1; then
  if codesign --verify "$PREFIX/$SERVICE_BIN" 2>/dev/null; then
    : # already validly signed (baked in at build time) — nothing to do
  elif [ -f "$ENTITLEMENTS" ] && \
       codesign --force --options runtime --sign - --entitlements "$ENTITLEMENTS" "$PREFIX/$SERVICE_BIN" 2>/dev/null; then
    echo "Signed companion with JIT entitlements (required on Apple Silicon)."
  else
    echo "Warning: companion is not validly signed and re-signing failed; it may be killed at startup." >&2
  fi
fi

# ── 3. Resolve ffmpeg: existing on PATH > bundled static binary > auto-install via Homebrew ─────────
if command -v ffmpeg >/dev/null 2>&1; then
  FFMPEG="$(command -v ffmpeg)"
elif [ -x "$PREFIX/ffmpeg" ]; then
  FFMPEG="$PREFIX/ffmpeg"
elif command -v brew >/dev/null 2>&1; then
  echo "ffmpeg not found - installing it with Homebrew (this can take a few minutes)..."
  brew install ffmpeg || true
  if command -v ffmpeg >/dev/null 2>&1; then
    FFMPEG="$(command -v ffmpeg)"
    echo "OK: installed ffmpeg at $FFMPEG"
  else
    echo "Warning: 'brew install ffmpeg' did not succeed; remux/DVR/recordings stay unavailable." >&2
    FFMPEG="ffmpeg"
  fi
else
  echo "Warning: no ffmpeg found and Homebrew is not installed (remux/DVR/recordings will be unavailable)." >&2
  echo "         Install Homebrew from https://brew.sh then re-run this installer, or: brew install ffmpeg" >&2
  FFMPEG="ffmpeg"
fi

# ── 4. Write + load the LaunchAgent ────────────────────────────────────────────────────────────────
# Substitute @PREFIX@/@FFMPEG@ in the bundled template (shipped inside the tarball).
sed -e "s#@PREFIX@#$PREFIX#g" -e "s#@FFMPEG@#$FFMPEG#g" \
    "$PREFIX/com.playlistmixer.companion.plist.template" > "$PLIST"

launchctl bootstrap "gui/$(id -u)" "$PLIST"
launchctl kickstart "gui/$(id -u)/$LABEL"

# ── 5. Confirm it came up ──────────────────────────────────────────────────────────────────────────
echo "Waiting for the companion to start..."
for _ in 1 2 3 4 5 6 7 8 9 10; do
  for PORT in 36400 36401 36402; do
    if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
      echo "OK: PlaylistMixer Companion is running on http://127.0.0.1:$PORT"
      echo "  Installed to: $PREFIX"
      echo "  Logs:         $PREFIX/companion.log"
      exit 0
    fi
  done
  sleep 1
done

echo "Installed, but the service didn't answer on 36400-36402 yet." >&2
echo "Check the log: $PREFIX/companion.log" >&2
exit 1
