# Draws src\app.ico: a power symbol on a Steam-coloured disc. Run once; the .ico is what gets built in.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\app.ico'
$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $disc = New-Object Drawing.RectangleF 0.5, 0.5, ($s - 1), ($s - 1)
    $fill = New-Object Drawing.Drawing2D.LinearGradientBrush $disc, ([Drawing.Color]::FromArgb(42, 71, 94)), ([Drawing.Color]::FromArgb(23, 26, 33)), 90
    $g.FillEllipse($fill, $disc)
    # Small sizes need relatively thicker strokes to stay readable.
    $weight = [Math]::Max($s * 0.11, 2.0)
    $accent = [Drawing.Color]::FromArgb(102, 192, 244)
    $pen = New-Object Drawing.Pen $accent, $weight
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $r = $s * 0.27; $c = $s / 2.0; $cy = $c + $s * 0.04
    $g.DrawArc($pen, ($c - $r), ($cy - $r), (2 * $r), (2 * $r), -55, 290)
    $g.DrawLine($pen, $c, ($s * 0.17), $c, ($cy - $s * 0.04))
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [IO.File]::Create($out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Close()
