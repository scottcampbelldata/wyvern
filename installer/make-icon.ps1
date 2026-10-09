# Draws Wyvern's icon and writes it to src\Wyvern.App\Wyvern.ico. The icon is kept in the
# repository; run this only to change how it looks:
#
#   powershell -ExecutionPolicy Bypass -File installer\make-icon.ps1
#
# The picture: a dark blue rounded square, an amber light in the middle, and a white arc on
# either side of it. The file holds it at eight sizes, the small ones as bitmaps and the
# largest as a PNG, which is how Windows expects an .ico to be laid out.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Wyvern.App\Wyvern.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function New-Picture([int]$size) {
    $picture = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($picture)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # The rounded square.
        $corner = [single]($size * 0.44)
        $edge = [single]($size - 0.01)
        $square = New-Object System.Drawing.Drawing2D.GraphicsPath
        $square.AddArc(0, 0, $corner, $corner, 180, 90)
        $square.AddArc($edge - $corner, 0, $corner, $corner, 270, 90)
        $square.AddArc($edge - $corner, $edge - $corner, $corner, $corner, 0, 90)
        $square.AddArc(0, $edge - $corner, $corner, $corner, 90, 90)
        $square.CloseFigure()
        $blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 30, 58, 95))
        $g.FillPath($blue, $square)

        # The two arcs.
        $radius = [single]($size * 0.33)
        $middle = [single]($size / 2.0)
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]([Math]::Max(1.4, $size * 0.09)))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($pen, $middle - $radius, $middle - $radius, 2 * $radius, 2 * $radius, -50, 100)
        $g.DrawArc($pen, $middle - $radius, $middle - $radius, 2 * $radius, 2 * $radius, 130, 100)

        # The light.
        $light = [single]($size * 0.15)
        $amber = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 176, 0))
        $g.FillEllipse($amber, $middle - $light, $middle - $light, 2 * $light, 2 * $light)

        $blue.Dispose(); $pen.Dispose(); $amber.Dispose(); $square.Dispose()
    } finally {
        $g.Dispose()
    }
    return $picture
}

# One picture as the bytes an .ico holds for it.
function Get-IconImage([System.Drawing.Bitmap]$picture) {
    $size = $picture.Width
    $memory = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $picture.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$memory.ToArray()
    }

    # A bitmap without its file header: the header says twice the height, then come the
    # pixels from the bottom row up, then a mask of one bit a pixel, which stays empty
    # because the pixels carry their own transparency.
    $area = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $picture.LockBits($area, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $pixels = New-Object byte[] ($size * $size * 4)
        for ($row = 0; $row -lt $size; $row++) {
            $from = [IntPtr]::Add($data.Scan0, $row * $data.Stride)
            [System.Runtime.InteropServices.Marshal]::Copy($from, $pixels, ($size - 1 - $row) * $size * 4, $size * 4)
        }
    } finally {
        $picture.UnlockBits($data)
    }

    $maskRow = [int]([Math]::Floor(($size + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskRow * $size)
    $writer = New-Object System.IO.BinaryWriter($memory)
    $writer.Write([int]40)
    $writer.Write([int]$size)
    $writer.Write([int]($size * 2))
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int]0)
    $writer.Write([int]($pixels.Length + $mask.Length))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    $writer.Write($pixels)
    $writer.Write($mask)
    $writer.Flush()
    return ,$memory.ToArray()
}

$images = foreach ($size in $sizes) {
    $picture = New-Picture $size
    try { ,(Get-IconImage $picture) } finally { $picture.Dispose() }
}

$file = New-Object System.IO.MemoryStream
$out = New-Object System.IO.BinaryWriter($file)
$out.Write([int16]0); $out.Write([int16]1); $out.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $out.Write([byte]$side); $out.Write([byte]$side); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([int16]1); $out.Write([int16]32)
    $out.Write([int]$images[$i].Length)
    $out.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $out.Write($image) }
$out.Flush()
[System.IO.File]::WriteAllBytes($target, $file.ToArray())
'{0}  ({1} sizes, {2:N0} bytes)' -f $target, $sizes.Count, $file.Length
