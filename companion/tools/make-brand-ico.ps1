# Generates brand.ico for the companion (exe + installer icon) from the web app logo artwork:
# a rounded persimmon tile (#E4572E -> #C8451B, 145deg) with the white "playlist lines + mixer knobs"
# glyph, matching web/public/favicon.svg (24-unit viewBox) and the runtime tray icon (TrayIcons.cs).
# Run with Windows PowerShell (has System.Drawing): powershell.exe -NoProfile -File make-brand-ico.ps1 <out.ico>
param([Parameter(Mandatory=$true)][string]$OutPath)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-BrandPng([int]$n) {
  $k = $n / 24.0
  $bmp = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)

  # Rounded persimmon tile (favicon rx=6 on a 24 box).
  $rad = 6.0 * $k
  $d = $rad * 2.0
  $w = $n - 1.0
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0.0, 0.0, $d, $d, 180, 90)
  $path.AddArc($w - $d, 0.0, $d, $d, 270, 90)
  $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
  $path.AddArc(0.0, $w - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  $rect = New-Object System.Drawing.RectangleF(0.0, 0.0, $w, $w)
  $c1 = [System.Drawing.Color]::FromArgb(0xE4, 0x57, 0x2E)
  $c2 = [System.Drawing.Color]::FromArgb(0xC8, 0x45, 0x1B)
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 145.0)
  $g.FillPath($grad, $path)

  # White glyph: three slider lines + three knobs (favicon coordinates x scale).
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single](2.0 * $k))
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($pen, [single](5 * $k), [single](7.5 * $k), [single](19 * $k), [single](7.5 * $k))
  $g.DrawLine($pen, [single](5 * $k), [single](12 * $k),  [single](19 * $k), [single](12 * $k))
  $g.DrawLine($pen, [single](5 * $k), [single](16.5 * $k),[single](19 * $k), [single](16.5 * $k))

  $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
  function Knob($cx, $cy) { $r = 2.1 * $k; $g.FillEllipse($white, [single]($cx * $k - $r), [single]($cy * $k - $r), [single]($r * 2), [single]($r * 2)) }
  Knob 15 7.5
  Knob 9 12
  Knob 12.5 16.5

  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $grad.Dispose(); $pen.Dispose(); $white.Dispose(); $path.Dispose(); $g.Dispose(); $bmp.Dispose()
  return ,$ms.ToArray()
}

$sizes = 16, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = New-BrandPng $s }

# Assemble the ICO container: ICONDIR header + one ICONDIRENTRY per image + concatenated PNG data.
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type = icon
$bw.Write([UInt16]$sizes.Count) # image count

$offset = 6 + (16 * $sizes.Count)
foreach ($s in $sizes) {
  $data = $pngs[$s]
  $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s }))) # width  (0 = 256)
  $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s }))) # height (0 = 256)
  $bw.Write([Byte]0)            # color count
  $bw.Write([Byte]0)            # reserved
  $bw.Write([UInt16]1)          # planes
  $bw.Write([UInt16]32)         # bpp
  $bw.Write([UInt32]$data.Length)
  $bw.Write([UInt32]$offset)
  $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Output ("Wrote {0} ({1} bytes, {2} sizes)" -f $OutPath, (Get-Item $OutPath).Length, $sizes.Count)
