#!/bin/sh
# Builds the macOS PlaylistMixer Companion release artifacts ON a Mac, signing each binary at build
# time. This is the canonical macOS-artifact build — prefer it over build-mac.ps1, whose Windows
# cross-publish cannot codesign Mach-O. Baking the ad-hoc signature in here means end users do NOT
# need Xcode Command Line Tools (codesign) on their machines for the service to start.
#
# For each architecture (arm64, x64) it:
#   1. Publishes the Service as a self-contained single-file binary (no .NET runtime needed on the Mac).
#   2. Ad-hoc signs it (hardened runtime + companion.entitlements) — required so the JIT-heavy app is
#      not SIGKILL'd by W^X on Apple Silicon. The embedded signature survives tar/curl/untar.
#   3. Stages the LaunchAgent plist template + entitlements (and, if present, a bundled static ffmpeg).
#   4. Packs it into ./output/companion-mac-<arch>.tar.gz  (fixed name — overwrite each release).
# Then it writes ./output/companion-version.json (the shared update manifest the SPA reads) and
# mirrors everything into web/public/downloads for local end-to-end testing.
#
# Optional ffmpeg bundling — drop a static macOS build into ./assets before running to embed it:
#     ./assets/ffmpeg-arm64   ./assets/ffmpeg-x64   ./assets/ffmpeg-LICENSE.txt
# (else the Mac uses its Homebrew ffmpeg, which install.sh prefers anyway.)
#
# Usage:  ./build-mac.sh [VERSION]      e.g.  ./build-mac.sh 1.6.0
set -eu

VERSION="${1:-1.0.0}"

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
ASSETS="$HERE/assets"
PLIST_TEMPLATE="$HERE/com.playlistmixer.companion.plist.template"
ENTITLEMENTS="$HERE/companion.entitlements"
SVC_PROJ="$REPO/companion/PlaylistMixer.Companion.Service/PlaylistMixer.Companion.Service.csproj"
SERVICE_BIN="PlaylistMixer.Companion.Service"
OUTPUT="$HERE/output"

# ── Preconditions ────────────────────────────────────────────────────────────────────────────────
[ "$(uname -s)" = "Darwin" ] || { echo "build-mac.sh must run on macOS (it codesigns the binaries)." >&2; exit 1; }
command -v dotnet   >/dev/null 2>&1 || { echo "dotnet not found on PATH." >&2; exit 1; }
command -v codesign >/dev/null 2>&1 || { echo "codesign not found — install Xcode Command Line Tools: xcode-select --install" >&2; exit 1; }
[ -f "$PLIST_TEMPLATE" ] || { echo "Missing $PLIST_TEMPLATE" >&2; exit 1; }
[ -f "$ENTITLEMENTS" ]   || { echo "Missing $ENTITLEMENTS" >&2; exit 1; }

rm -rf "$OUTPUT"
mkdir -p "$OUTPUT"

# rid = .NET runtime identifier; arch = suffix used in the tarball name / install.sh.
for target in "osx-arm64:arm64" "osx-x64:x64"; do
  rid="${target%%:*}"; arch="${target##*:}"
  stage="$OUTPUT/$rid"
  echo "Publishing service for $rid ..."
  dotnet publish "$SVC_PROJ" \
    -c Release -r "$rid" --self-contained true \
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true \
    /p:DebugType=none /p:DebugSymbols=false \
    /p:Version="$VERSION" \
    -o "$stage" >/dev/null
  rm -f "$stage"/*.staticwebassets.endpoints.json

  # Ad-hoc sign with the JIT entitlements + hardened runtime. Required on arm64; harmless on x64 (kept
  # uniform). Entitlements are IGNORED without --options runtime, so both flags are mandatory.
  codesign --force --options runtime --sign - --entitlements "$ENTITLEMENTS" "$stage/$SERVICE_BIN"
  codesign --verify --strict "$stage/$SERVICE_BIN" || { echo "signature verify failed for $rid" >&2; exit 1; }
  echo "  signed ($arch) with JIT entitlements"

  # Stage the LaunchAgent template + entitlements (install.sh reads both from the unpacked tarball).
  cp "$PLIST_TEMPLATE" "$stage/"
  cp "$ENTITLEMENTS"   "$stage/"

  # Bundle the static ffmpeg for this arch + license if present (optional — install.sh resolves
  # ffmpeg defensively: PATH > bundled > Homebrew).
  ff="$ASSETS/ffmpeg-$arch"
  if [ -f "$ff" ]; then
    cp "$ff" "$stage/ffmpeg"
    [ -f "$ASSETS/ffmpeg-LICENSE.txt" ] && cp "$ASSETS/ffmpeg-LICENSE.txt" "$stage/"
    echo "  bundled ffmpeg-$arch"
  else
    echo "  (no $ff — tarball ships without a bundled ffmpeg; install.sh will use PATH/Homebrew)"
  fi

  tarball="$OUTPUT/companion-mac-$arch.tar.gz"
  echo "Packing $tarball ..."
  tar -czf "$tarball" -C "$stage" .
  rm -rf "$stage"
done

# The install/uninstall scripts are static — copy them into the deployable set so they land at
# /downloads/install.sh (the URL the SPA hands users). Without this they 404.
cp "$HERE/install.sh"   "$OUTPUT/"
cp "$HERE/uninstall.sh" "$OUTPUT/"

# Shared update manifest (same single-version format build.ps1 writes; the SPA's useCompanionUpdate
# compares the running companion's version to this regardless of OS).
printf '{\n  "version": "%s"\n}\n' "$VERSION" > "$OUTPUT/companion-version.json"

# Mirror into the dev server's static downloads (web/public is served at / by Vite) for E2E testing.
DEV_DOWNLOADS="$REPO/web/public/downloads"
mkdir -p "$DEV_DOWNLOADS"
cp "$OUTPUT"/* "$DEV_DOWNLOADS/"

echo "Done. Deploy these to the site's /downloads/:"
for f in "$OUTPUT"/*; do echo "  - $(basename "$f")"; done
echo "(install.sh + uninstall.sh in this folder are deployed once as static files.)"
