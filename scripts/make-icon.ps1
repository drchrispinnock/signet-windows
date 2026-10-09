<#
.SYNOPSIS
  Builds src/Signet.App/Assets/Signet.ico (and Signet.png) from the Mac app's master icon.

.DESCRIPTION
  The icon is upstream's `spec/icon/Tezos-Signet-1024.png`; rerun this whenever it changes.
  Each size Windows asks for (16 to 256 px) is resampled from the master and stored as a PNG
  entry, so small sizes are not left to Windows' own downscaling.
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Join-Path $PSScriptRoot ".."
$master = Join-Path $root "upstream\spec\icon\Tezos-Signet-1024.png"
$assets = Join-Path $root "src\Signet.App\Assets"
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Resize([System.Drawing.Image]$image, [int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($image, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ,$ms.ToArray()
}

$image = [System.Drawing.Image]::FromFile((Resolve-Path $master))
try {
    $entries = foreach ($s in $sizes) { ,(Resize $image $s) }
    [IO.File]::WriteAllBytes((Join-Path $assets "Signet.png"), (Resize $image 256))
} finally {
    $image.Dispose()
}

$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 means 256 in the ICO header
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$entries[$i].Length); $w.Write([uint32]$offset)
    $offset += $entries[$i].Length
}
foreach ($e in $entries) { $w.Write($e) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets "Signet.ico"), $ms.ToArray())
Write-Output "Wrote Signet.ico ($($sizes -join ', ') px) and Signet.png from $master"
