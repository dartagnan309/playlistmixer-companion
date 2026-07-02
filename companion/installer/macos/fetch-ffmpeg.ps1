<#
  Fetches the static macOS ffmpeg binaries that build-mac.ps1 bundles into the companion tarballs,
  into .\assets\ (gitignored — never committed). Run once before build-mac.ps1; re-run to update.

  Source: eugeneware/ffmpeg-static GitHub releases — self-contained static Mach-O builds (no dylibs),
  which is what the companion needs since it shells out to ffmpeg from a self-contained app dir.
  Version + SHA-256 are PINNED below; the script refuses a download whose hash doesn't match, so a
  tampered or swapped release can't slip into a shipped build. Bump $Version + the hashes to update.

  Usage:  pwsh .\fetch-ffmpeg.ps1
#>
param(
    [string]$Version = "b6.1.1"
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$assets = Join-Path $here "assets"
New-Item -ItemType Directory -Path $assets -Force | Out-Null

$base = "https://github.com/eugeneware/ffmpeg-static/releases/download/$Version"

# Pinned artifacts: release asset name -> local file name + expected SHA-256 (ffmpeg 6.1.1 builds).
$targets = @(
    @{ asset = "ffmpeg-darwin-arm64"; out = "ffmpeg-arm64"; sha = "a90e3db6a3fd35f6074b013f948b1aa45b31c6375489d39e572bea3f18336584" },
    @{ asset = "ffmpeg-darwin-x64";   out = "ffmpeg-x64";   sha = "ebdddc936f61e14049a2d4b549a412b8a40deeff6540e58a9f2a2da9e6b18894" }
)

foreach ($t in $targets) {
    $dest = Join-Path $assets $t.out
    Write-Host "Downloading $($t.asset) ($Version)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "$base/$($t.asset)" -OutFile $dest -UseBasicParsing
    $actual = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $t.sha) {
        Remove-Item $dest -Force
        throw "SHA-256 mismatch for $($t.asset):`n  expected $($t.sha)`n  actual   $actual`nRefusing to keep an unverified binary."
    }
    Write-Host "  verified $($t.out) (sha256 ok)" -ForegroundColor DarkGray
}

# License text (GPL — these builds include GPL components; ship it for redistribution).
Invoke-WebRequest -Uri "$base/darwin-arm64.LICENSE" -OutFile (Join-Path $assets "ffmpeg-LICENSE.txt") -UseBasicParsing

Write-Host "Done. assets\ffmpeg-arm64, ffmpeg-x64, ffmpeg-LICENSE.txt are ready for build-mac.ps1." -ForegroundColor Green
