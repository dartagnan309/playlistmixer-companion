# PlaylistMixer Companion — macOS install

The macOS Companion runs as a per-user **LaunchAgent** (not a system daemon), so it lives in the
user's GUI session and can launch a local player with `open`. There is no system service and no admin
rights are required.

## Install (end user)

Copy the exact one-liner from the app's **Companion** page — it targets whatever domain serves the
SPA (it sets `PLAYLISTMIXER_BASE_URL` to that origin's `/downloads`). It looks like:

```sh
curl -fsSL https://playlistmixer.top/downloads/install.sh | PLAYLISTMIXER_BASE_URL="https://playlistmixer.top/downloads" sh
```

This downloads the self-contained service for your Mac's architecture, installs it under
`~/Library/Application Support/PlaylistMixer Companion`, and registers the LaunchAgent
(`~/Library/LaunchAgents/com.playlistmixer.companion.plist`). It starts at login and is kept alive.

Re-running the command upgrades in place. Remove with `uninstall.sh`.

> Why no *notarization* is needed: files fetched with `curl`/`tar` are **not** quarantined, so
> Gatekeeper never evaluates them. (A browser-downloaded `.dmg` *would* be quarantined and would
> require notarization.)
>
> Why an *ad-hoc signature* still is (Apple Silicon): the service is a self-contained .NET app that
> JITs heavily at startup, and arm64 macOS enforces W^X — it SIGKILLs a process that allocates
> executable memory unless it's signed with the hardened runtime **and** JIT entitlements (see
> `companion.entitlements`). **`build-mac.sh` bakes this ad-hoc signature in at build time**, and the
> embedded signature survives `tar`/`curl`/untar — so end users need **nothing** (no Xcode Command
> Line Tools, no `codesign`) for the service to start. Ad-hoc (`codesign --sign -`) is sufficient:
> still no Apple Developer ID or notarization. `install.sh` only re-signs as a *fallback* if the
> binary somehow arrived unsigned (e.g. an old Windows-built tarball) and `codesign` happens to be
> present. Intel Macs are unaffected. (Note: `build-mac.ps1`'s Windows cross-build produces
> **unsigned** binaries — fine on Intel, but on Apple Silicon prefer `build-mac.sh`.)

## Build the release artifacts (maintainer)

Run on a **Mac** (so the binaries are codesigned at build time — required for Apple Silicon; see the
signing note above):

```sh
companion/installer/macos/build-mac.sh 1.6.0
```

This publishes the self-contained binaries, ad-hoc signs each with `companion.entitlements`, packs
the tarballs, writes `companion-version.json`, and mirrors everything into `web/public/downloads`.
Optionally drop static ffmpeg builds into `assets/` (`ffmpeg-arm64`, `ffmpeg-x64`,
`ffmpeg-LICENSE.txt`) first to bundle them; otherwise the tarball ships without ffmpeg and the
install resolves it from `PATH`/Homebrew.

> Legacy: `build-mac.ps1` cross-publishes from **Windows** (no Mac required) but cannot codesign, so
> its Apple Silicon tarball relies on `install.sh`'s fallback re-sign (needs `codesign` on the user's
> machine). Prefer `build-mac.sh` for releases.

It produces, in `companion/installer/macos/output/`:

```
companion-mac-arm64.tar.gz     # Apple Silicon
companion-mac-x64.tar.gz       # Intel
companion-version.json         # { "version": "1.6.0" } — shared with Windows; read by the update banner
```

Each tarball unpacks flat into `$PREFIX` and contains:

```
PlaylistMixer.Companion.Service             # self-contained single-file binary (ad-hoc signed by build-mac.sh)
appsettings.json
com.playlistmixer.companion.plist.template  # LaunchAgent template (install.sh substitutes + installs it)
companion.entitlements                       # JIT entitlements baked into the signature (also install.sh's fallback)
ffmpeg + ffmpeg-LICENSE.txt                 # only if a static build was bundled (see below)
```

**Tarball names are fixed** (no version suffix), exactly like `PlaylistMixer-Companion-Setup.exe`:
the operator overwrites them each release, and `companion-version.json` is what nudges users (via the
in-app update banner) to re-run `install.sh`. Because the version manifest is shared, **no SPA change
is needed** — the existing `useCompanionUpdate` compares the running companion's version to it
regardless of OS.

Deploy everything in `output/` to the site's `/downloads/`, alongside `install.sh` / `uninstall.sh`
(those are deployed once as static files). `build-mac.sh` also mirrors the artifacts into
`web/public/downloads/` for local end-to-end testing.

### ffmpeg

A static ffmpeg is **bundled in the tarball** by default — `fetch-ffmpeg.ps1` downloads pinned,
checksum-verified static builds (eugeneware/ffmpeg-static `b6.1.1`) into `assets\`, and
`build-mac.ps1` requires them. This gives a zero-dependency install: no Homebrew, no ~1 GB brew pull,
works offline. The GPL license text ships alongside as `ffmpeg-LICENSE.txt`.

`install.sh` still resolves defensively in order: (1) an ffmpeg already on `PATH`, (2) the bundled
binary, (3) auto-install via Homebrew if `brew` is present — so even an old tarball without a bundled
binary degrades gracefully. To bump the bundled version, edit the pinned version + SHA-256 in
`fetch-ffmpeg.ps1` and re-run it.

## What differs from Windows

| | Windows | macOS |
| --- | --- | --- |
| Lifecycle | LocalSystem service (`sc create`, session 0) | per-user LaunchAgent (`launchctl`, user session) |
| Auto-start / restart | `start= auto` + failure actions | `RunAtLoad` + `KeepAlive` |
| Player launch | `CreateProcessAsUser` into the active desktop | `open -a` (already in the user session) |
| Pairing secret | DPAPI (LocalMachine) | `~/Library/...` file, `chmod 0600` |
| Runs when logged out | yes | no (LaunchAgent needs a session; required so it can launch a GUI player) |
