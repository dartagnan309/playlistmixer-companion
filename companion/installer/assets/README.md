# Installer assets

Drop these here before running `build.ps1` (they are **not** committed):

- `ffmpeg.exe` — a static FFmpeg **win-x64** build. The service shells out to it for remuxing.
  Get one from https://www.gyan.dev/ffmpeg/builds/ or https://github.com/BtbN/FFmpeg-Builds.
- `ffmpeg-LICENSE.txt` — FFmpeg's license text. **Required** when redistributing FFmpeg
  (GPL/LGPL). Copy the `LICENSE`/`COPYING` file from the FFmpeg build you used.

`build.ps1` copies both into the installed app folder.
