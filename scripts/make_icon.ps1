# Builds a multi-resolution .ico (PNG-compressed entries, Vista+) from a source PNG.
param(
  [string]$SourcePng,
  [string]$OutputIco
)

Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile((Resolve-Path $SourcePng))
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$entries = @()
$payloads = @()
$offset = 6 + (16 * $sizes.Count) # ICONDIR + ICONDIRENTRY per image

foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($src, 0, 0, $s, $s)
  $g.Dispose()

  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngBytes = $ms.ToArray()
  $ms.Dispose()
  $bmp.Dispose()

  $sizeByte = if ($s -ge 256) { 0 } else { $s }
  $entry = New-Object byte[] (16)
  $entry[0] = $sizeByte            # width (0 = 256)
  $entry[1] = $sizeByte            # height
  $entry[2] = 0                    # color count
  $entry[3] = 0                    # reserved
  $entry[4] = 1; $entry[5] = 0     # planes (LE)
  $entry[6] = 32; $entry[7] = 0    # bit count (LE)
  $len = $pngBytes.Length
  $entry[8]  = $len -band 0xFF            # bytes in resource (LE)
  $entry[9]  = ($len -shr 8) -band 0xFF
  $entry[10] = ($len -shr 16) -band 0xFF
  $entry[11] = ($len -shr 24) -band 0xFF
  $entry[12] = $offset -band 0xFF         # image offset (LE)
  $entry[13] = ($offset -shr 8) -band 0xFF
  $entry[14] = ($offset -shr 16) -band 0xFF
  $entry[15] = ($offset -shr 24) -band 0xFF

  $entries += , $entry
  $payloads += , $pngBytes
  $offset += $len
}

$header = New-Object byte[] (6)
$header[0] = 0; $header[1] = 0                          # reserved
$header[2] = 1; $header[3] = 0                          # type: icon
$header[4] = $sizes.Count -band 0xFF
$header[5] = ($sizes.Count -shr 8) -band 0xFF

$fs = New-Object System.IO.FileStream($OutputIco, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write($header)
foreach ($e in $entries) { $bw.Write($e) }
foreach ($p in $payloads) { $bw.Write($p) }
$bw.Close()
$src.Dispose()

Write-Host "Wrote $OutputIco ($offset bytes, $($sizes.Count) resolutions)"