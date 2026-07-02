# PlaylistMixer Companion

A small local app that offloads stream proxying, FFmpeg remux, live recording, and a rewind DVR
to your own machine, so playback in a PlaylistMixer server instance costs the server no bandwidth
or CPU. It runs as a background service (Windows service / macOS LaunchAgent) and is discovered by
the web app over a fixed loopback port. It can also pair with your server to relay upstream provider
requests from your home IP.

Runs on **Windows 10/11 (x64)** and **macOS (Apple Silicon + Intel)**.

## Install

- **Windows:** download and run the latest
  [`PlaylistMixer-Companion-Setup.exe`](https://github.com/dartagnan309/playlistmixer-companion/releases/latest).
- **macOS:** paste into Terminal —
  ```
  curl -fsSL https://github.com/dartagnan309/playlistmixer-companion/releases/latest/download/install.sh \
    | PLAYLISTMIXER_BASE_URL="https://github.com/dartagnan309/playlistmixer-companion/releases/latest/download" sh
  ```

The Windows installer is currently unsigned; macOS binaries are ad-hoc codesigned with JIT
entitlements (required on Apple Silicon), so no Xcode tools are needed at install time.

## Build from source

```bash
git clone --recursive https://github.com/dartagnan309/playlistmixer-companion.git
cd playlistmixer-companion
dotnet build PlaylistMixer.Companion.sln -c Release   # Windows (Tray is WinForms/net9.0-windows)
```
`PlaylistMixer.Playback` is a git submodule — `--recursive` (or `git submodule update --init`) is
required.

Installers: `companion/installer/build.ps1` (Windows, needs Inno Setup 6 + a static
`assets/ffmpeg.exe`) and `companion/installer/macos/build-mac.sh` (macOS, run on a Mac).

## Support

If this project is useful to you, see [Sponsor](https://github.com/sponsors/dartagnan309).

## License

MIT — see [LICENSE](LICENSE).
