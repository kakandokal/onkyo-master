# Generates src/OnkyoMaster/Assets/app.ico: a volume dial on a dark rounded square.
# Run once; the .ico is checked in next to the project.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\OnkyoMaster\Assets\app.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-DialPng([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([Drawing.Color]::Transparent)

    # Rounded-square background
    $r = [single]($size * 0.22)
    $d = $r * 2
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 32, 33, 38))
    $g.FillPath($bg, $path)

    # Dial: grey 270-degree track, orange arc for the level, dot in the middle
    $stroke = [single][Math]::Max(1.6, $size * 0.11)
    $inset = [single]($size * 0.2)
    $rect = New-Object Drawing.RectangleF $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)
    $track = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 92, 94, 104)), $stroke
    $track.StartCap = $track.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($track, $rect, 135, 270)
    $accent = [Drawing.Color]::FromArgb(255, 255, 140, 30)
    $arc = New-Object Drawing.Pen $accent, $stroke
    $arc.StartCap = $arc.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($arc, $rect, 135, 170)
    $dot = [single]($size * 0.16)
    $g.FillEllipse((New-Object Drawing.SolidBrush $accent), ($size - $dot) / 2, ($size - $dot) / 2, $dot, $dot)

    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# ICO container with PNG-compressed frames (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 48, 64, 256
$images = $sizes | ForEach-Object { , (New-DialPng $_) }
$ico = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $ico.ToArray())
Write-Output "Wrote $([IO.Path]::GetFullPath($Out)) ($($ico.Length) bytes)"
