# Convert the reviewed generated master into PNG-backed Windows icon frames and the WPF source.
# This is deterministic asset preparation, not image generation. See branding/waypoint-artwork.json.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../branding'))
$master = [Drawing.Bitmap]::new((Join-Path $directory 'waypoint-master.png'))
function Resize-Artwork([int]$size) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $attributes = [Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($master, [Drawing.Rectangle]::new(0,0,$size,$size), 0,0,$master.Width,$master.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
        $stream = [IO.MemoryStream]::new()
        try { $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); return ,$stream.ToArray() }
        finally { $stream.Dispose() }
    } finally { $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
try {
    [IO.File]::WriteAllBytes((Join-Path $directory 'waypoint-192.png'), (Resize-Artwork 192))
    $sizes = @(16,20,24,32,40,48,64,96,128,256)
    $frames = @($sizes | ForEach-Object { Resize-Artwork $_ })
    $writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $directory 'app.ico')))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
} finally { $master.Dispose() }
