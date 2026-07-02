<#
  Builds the PlaylistMixer Companion installer.

  1. Publishes the service + tray as self-contained single-file win-x64 exes (no .NET runtime needed
     on the user's machine — true install-and-forget).
  2. Stages them, the bundled ffmpeg.exe, and license text into .\stage.
  3. Writes .\output\companion-version.json (the update-check manifest the SPA reads).
  4. Compiles the Inno Setup script (requires Inno Setup 6 / ISCC.exe on PATH or at the default path).

  Deploy BOTH .\output\PlaylistMixer-Companion-Setup.exe and .\output\companion-version.json to the
  site's /downloads/ folder so the SPA can offer the installer and detect when users are out of date.

  Before running, drop a static FFmpeg build here:
      .\assets\ffmpeg.exe          (the binary the service shells out to)
      .\assets\ffmpeg-LICENSE.txt  (FFmpeg's license text — required for GPL/LGPL redistribution)

  Usage:  pwsh .\build.ps1 [-Version 1.0.0]
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Resolve-Path (Join-Path $here "..\..")
$stage = Join-Path $here "stage"
$assets = Join-Path $here "assets"

$ffmpeg = Join-Path $assets "ffmpeg.exe"
if (-not (Test-Path $ffmpeg)) {
    throw "Missing $ffmpeg. Place a static FFmpeg win-x64 build there (and ffmpeg-LICENSE.txt) before building."
}

# Clean stage
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

$publishCommon = @(
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "/p:PublishSingleFile=true", "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:DebugType=none", "/p:DebugSymbols=false", # no .pdb in the shipped payload
    "/p:Version=$Version"
)

Write-Host "Publishing service..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo "companion\PlaylistMixer.Companion.Service\PlaylistMixer.Companion.Service.csproj") `
    @publishCommon -o (Join-Path $stage "svc")

Write-Host "Publishing tray..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo "companion\PlaylistMixer.Companion.Tray\PlaylistMixer.Companion.Tray.csproj") `
    @publishCommon -o (Join-Path $stage "tray")

# Flatten the two publishes + ffmpeg + license into one app folder.
$app = Join-Path $stage "app"
New-Item -ItemType Directory -Path $app | Out-Null
Copy-Item (Join-Path $stage "svc\*") $app -Recurse -Force
Copy-Item (Join-Path $stage "tray\*") $app -Recurse -Force
Copy-Item $ffmpeg $app -Force
$ffLicense = Join-Path $assets "ffmpeg-LICENSE.txt"
if (Test-Path $ffLicense) { Copy-Item $ffLicense $app -Force }

# Emit the version manifest the SPA reads to nudge users to update (useCompanionUpdate in companion.ts).
# Deploy this to /downloads/companion-version.json alongside the installer. Sourced from the same
# -Version that stamps the assembly, so the published version and the manifest never drift.
$output = Join-Path $here "output"
New-Item -ItemType Directory -Path $output -Force | Out-Null
$versionJson = Join-Path $output "companion-version.json"
[pscustomobject]@{ version = $Version } | ConvertTo-Json | Set-Content -Path $versionJson -Encoding utf8
Write-Host "Wrote version manifest: $versionJson" -ForegroundColor Cyan

# Mirror the manifest into the local dev server's static downloads (web/public is served at / by Vite,
# so the SPA fetches /downloads/companion-version.json there). Done now — before the optional installer
# compile — so the manifest is in place even when ISCC isn't installed. Folder is gitignored.
$devDownloads = Join-Path $repo "web\public\downloads"
New-Item -ItemType Directory -Path $devDownloads -Force | Out-Null
Copy-Item $versionJson $devDownloads -Force
Write-Host "Synced version manifest to dev server: $devDownloads" -ForegroundColor Cyan

# Compile the installer.
$iss = Join-Path $here "PlaylistMixerCompanion.iss"
$iscc = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue)?.Source
if (-not $iscc) {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe") # winget per-user install
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    Write-Warning "ISCC.exe not found. Staged files are in $app (version manifest already written to $output and synced to the dev server). Install Inno Setup 6 and run: ISCC /DMyAppVersion=$Version `"$iss`""
    return
}

Write-Host "Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" $iss

# Mirror the freshly built installer into the dev server's downloads too, so a locally running SPA
# (Vite :5173) serves both the installer and the manifest from /downloads for end-to-end testing.
$setupExe = Join-Path $output "PlaylistMixer-Companion-Setup.exe"
Copy-Item $setupExe $devDownloads -Force
Write-Host "Synced installer to dev server: $devDownloads" -ForegroundColor Cyan

Write-Host "Done. Deploy both files in $output to the site's /downloads/:" -ForegroundColor Green
Write-Host "  - PlaylistMixer-Companion-Setup.exe" -ForegroundColor Green
Write-Host "  - companion-version.json" -ForegroundColor Green
