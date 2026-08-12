# Generates src/Tally.App/Assets/tally.ico — four tally marks + diagonal fifth stroke,
# white on a teal rounded square. BMP frames for small sizes (max GDI compatibility),
# PNG frame for 256. Rerun to regenerate after tweaking the drawing.
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\Tally.App\Assets\tally.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-TallyBitmap([int] $Size) {
    $bmp = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background with a vertical teal gradient
    $radius = [Math]::Max(2.0, $Size * 0.22)
    $d = [single]($radius * 2)
    $rect = [System.Drawing.RectangleF]::new(0, 0, $Size, $Size)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $top = [System.Drawing.Color]::FromArgb(255, 18, 168, 145)
    $bottom = [System.Drawing.Color]::FromArgb(255, 8, 100, 88)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $top, $bottom, [single]90)
    $g.FillPath($brush, $path)

    # Tally marks: four verticals + the diagonal fifth stroke
    $penWidth = [single][Math]::Max(1.4, $Size * 0.075)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $yTop = [single]($Size * 0.26)
    $yBottom = [single]($Size * 0.74)
    foreach ($x in 0.28, 0.4267, 0.5733, 0.72) {
        $g.DrawLine($pen, [single]($Size * $x), $yTop, [single]($Size * $x), $yBottom)
    }
    $g.DrawLine($pen,
        [single]($Size * 0.17), [single]($Size * 0.71),
        [single]($Size * 0.83), [single]($Size * 0.29))

    $pen.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $g.Dispose()
    return $bmp
}

function ConvertTo-PngBytes([System.Drawing.Bitmap] $Bitmap) {
    $ms = [System.IO.MemoryStream]::new()
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# 32bpp DIB (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask) — the classic ICO payload.
function ConvertTo-DibBytes([System.Drawing.Bitmap] $Bitmap) {
    $s = $Bitmap.Width
    $rect = [System.Drawing.Rectangle]::new(0, 0, $s, $s)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = [byte[]]::new($data.Stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $Bitmap.UnlockBits($data)

    $maskStride = ([int][Math]::Ceiling($s / 8.0) + 3) -band (-bnot 3)
    $ms = [System.IO.MemoryStream]::new()
    $w = [System.IO.BinaryWriter]::new($ms)
    $w.Write([uint32]40)              # BITMAPINFOHEADER size
    $w.Write([int32]$s)               # width
    $w.Write([int32]($s * 2))         # height (XOR + AND mask)
    $w.Write([uint16]1)               # planes
    $w.Write([uint16]32)              # bpp
    $w.Write([uint32]0)               # BI_RGB
    $w.Write([uint32]($s * $s * 4))   # image size
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        $w.Write($pixels, $y * $data.Stride, $s * 4)
    }
    $w.Write([byte[]]::new($maskStride * $s))   # AND mask: all zero, alpha rules
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 64, 256
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($s in $sizes) {
    $bmp = New-TallyBitmap $s
    if ($s -ge 256) { $frames.Add((ConvertTo-PngBytes $bmp)) }
    else { $frames.Add((ConvertTo-DibBytes $bmp)) }
    $bmp.Dispose()
}

$dir = Split-Path $OutputPath -Parent
New-Item -ItemType Directory -Force $dir | Out-Null
$fs = [System.IO.FileStream]::new($OutputPath, [System.IO.FileMode]::Create)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type: icon
$bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))   # width (0 = 256)
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))   # height
    $bw.Write([byte]0)               # palette
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # planes
    $bw.Write([uint16]32)            # bpp
    $bw.Write([uint32]$frames[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $bw.Write($frame) }
$bw.Dispose()
$fs.Dispose()
Write-Host "Wrote $OutputPath ($($sizes.Count) frames)"
