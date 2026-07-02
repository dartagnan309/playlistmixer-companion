<#
  Fetches the static win-x64 ffmpeg that build.ps1 bundles, into .\assets\ffmpeg.exe (gitignored).
  Source: eugeneware/ffmpeg-static (self-contained static build), the same release the macOS fetch
  pins. The download is verified against a pinned SHA-256; a mismatch aborts the build.
  Usage:  pwsh .\fetch-ffmpeg-win.ps1
#>
param(
    [string]$Version = "b6.1.1",
    [string]$Asset   = "ffmpeg-win32-x64",
    [string]$Sha     = "04e1307997530f9cf2fe35cba2ca7e8875ca91da02f89d6c7243df819c94ad00"
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$assets = Join-Path $here "assets"
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$base = "https://github.com/eugeneware/ffmpeg-static/releases/download/$Version"
$dest = Join-Path $assets "ffmpeg.exe"
Write-Host "Downloading $Asset ($Version)..." -ForegroundColor Cyan
Invoke-WebRequest -Uri "$base/$Asset" -OutFile $dest -UseBasicParsing
$actual = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLower()
if ($Sha -ne "PINNED_IN_STEP_2" -and $actual -ne $Sha) {
    Remove-Item $dest -Force
    throw "SHA-256 mismatch for ${Asset}: expected $Sha, actual $actual."
}
Invoke-WebRequest -Uri "$base/win32-x64.LICENSE" -OutFile (Join-Path $assets "ffmpeg-LICENSE.txt") -UseBasicParsing
Write-Host "Done. assets\ffmpeg.exe ($actual) + ffmpeg-LICENSE.txt ready." -ForegroundColor Green
