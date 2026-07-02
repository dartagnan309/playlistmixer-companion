<#
  Builds the macOS PlaylistMixer Companion release artifacts (the Windows counterpart is
  ..\build.ps1). The macOS Companion is just the Service — it runs as a per-user LaunchAgent; the
  menu-bar UI is a separate (future) project. There is no installer binary: distribution is the
  curl|sh script in this folder, which downloads one of the tarballs this script produces.

  For each architecture (arm64, x64) it:
    1. Publishes the Service as a self-contained single-file binary (no .NET runtime needed on the Mac).
    2. Stages the LaunchAgent plist template (and, if present, a bundled static ffmpeg + license).
    3. Packs it into .\output\companion-mac-<arch>.tar.gz  (fixed name — overwrite each release).
  Then it writes .\output\companion-version.json (the shared update manifest the SPA reads) and
  mirrors everything into web\public\downloads for local end-to-end testing.

  This runs on Windows (it cross-publishes the macOS binaries); `tar` ships with Windows 10+. The
  install.sh `chmod +x`es the binaries after extraction, so the exec bit not surviving the Windows
  tar is fine.

  Optional ffmpeg bundling — drop a static macOS build before running to embed it in the tarball
  (else the Mac uses its Homebrew ffmpeg, which install.sh prefers anyway):
      .\assets\ffmpeg-arm64        + .\assets\ffmpeg-LICENSE.txt
      .\assets\ffmpeg-x64          + .\assets\ffmpeg-LICENSE.txt

  Deploy ALL of .\output\* to the site's /downloads/ folder.

  Usage:  pwsh .\build-mac.ps1 [-Version 1.6.0]
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Resolve-Path (Join-Path $here "..\..\..")
$assets = Join-Path $here "assets"
$plistTemplate = Join-Path $here "com.playlistmixer.companion.plist.template"
$entitlements = Join-Path $here "companion.entitlements"
$svcProj = Join-Path $repo "companion\PlaylistMixer.Companion.Service\PlaylistMixer.Companion.Service.csproj"

if (-not (Test-Path $plistTemplate)) { throw "Missing $plistTemplate" }
if (-not (Test-Path $entitlements))  { throw "Missing $entitlements" }
if (-not (Get-Command tar -ErrorAction SilentlyContinue)) { throw "tar not found (ships with Windows 10+)." }

$output = Join-Path $here "output"
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item -ItemType Directory -Path $output | Out-Null

# rid = the .NET runtime identifier; arch = the suffix used in the tarball name / install.sh.
$targets = @(
    @{ rid = "osx-arm64"; arch = "arm64" },
    @{ rid = "osx-x64";   arch = "x64"   }
)

foreach ($t in $targets) {
    $rid = $t.rid; $arch = $t.arch
    $stage = Join-Path $output $rid
    Write-Host "Publishing service for $rid ..." -ForegroundColor Cyan
    dotnet publish $svcProj `
        -c Release -r $rid --self-contained true `
        /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:DebugType=none /p:DebugSymbols=false `
        /p:Version=$Version `
        -o $stage
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    # Stage the LaunchAgent template (install.sh reads it from the unpacked tarball).
    Copy-Item $plistTemplate $stage -Force

    # Stage the codesign entitlements. install.sh ad-hoc re-signs the binary with these after
    # unpacking — required so the JIT-heavy self-contained app isn't SIGKILL'd by W^X on Apple
    # Silicon (Windows can't codesign Mach-O, so this is deferred to the on-device install).
    Copy-Item $entitlements $stage -Force

    # Bundle the static ffmpeg for this arch + its license (GPL redistribution). Required — fetch the
    # binaries first with fetch-ffmpeg.ps1. (Mirrors how build.ps1 requires assets\ffmpeg.exe.)
    $ff = Join-Path $assets "ffmpeg-$arch"
    if (-not (Test-Path $ff)) {
        throw "Missing $ff. Run .\fetch-ffmpeg.ps1 first to download the static macOS ffmpeg binaries."
    }
    Copy-Item $ff (Join-Path $stage "ffmpeg") -Force
    $ffLicense = Join-Path $assets "ffmpeg-LICENSE.txt"
    if (Test-Path $ffLicense) { Copy-Item $ffLicense $stage -Force }
    Write-Host "  bundled ffmpeg-$arch" -ForegroundColor DarkGray

    $tarball = Join-Path $output "companion-mac-$arch.tar.gz"
    Write-Host "Packing $tarball ..." -ForegroundColor Cyan
    tar -czf $tarball -C $stage .
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
    Remove-Item $stage -Recurse -Force   # keep only the tarballs in output
}

# The install/uninstall scripts are static — copy them into the deployable set so they land at
# /downloads/install.sh (the URL the SPA hands users). Without this they 404.
Copy-Item (Join-Path $here "install.sh")   $output -Force
Copy-Item (Join-Path $here "uninstall.sh") $output -Force

# Shared update manifest (same single-version format build.ps1 writes; the SPA's useCompanionUpdate
# compares the running companion's version to this regardless of OS).
$versionJson = Join-Path $output "companion-version.json"
[pscustomobject]@{ version = $Version } | ConvertTo-Json | Set-Content -Path $versionJson -Encoding utf8

# Mirror into the dev server's static downloads (web\public is served at / by Vite) for E2E testing.
$devDownloads = Join-Path $repo "web\public\downloads"
New-Item -ItemType Directory -Path $devDownloads -Force | Out-Null
Copy-Item (Join-Path $output "*") $devDownloads -Force

Write-Host "Done. Deploy these to the site's /downloads/:" -ForegroundColor Green
Get-ChildItem $output | ForEach-Object { Write-Host "  - $($_.Name)" -ForegroundColor Green }
Write-Host "(install.sh + uninstall.sh in this folder are deployed once as static files.)" -ForegroundColor DarkGray
