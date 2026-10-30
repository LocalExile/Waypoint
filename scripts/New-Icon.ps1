# Deterministic geometric desktop icon. No font or external artwork required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new(64,64)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::FromArgb(16,21,30))
$pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(164,231,196),6)
$pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($pen,18,45,45,18)
$graphics.DrawLine($pen,27,18,45,18)
$graphics.DrawLine($pen,45,18,45,36)
$graphics.FillEllipse([Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(98,137,183)),14,14,9,9)
$stream = [IO.MemoryStream]::new()
$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
$png = $stream.ToArray()
$output = [IO.File]::Create((Join-Path $PSScriptRoot '../branding/app.ico'))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
    $writer.Write($png)
} finally { $writer.Dispose(); $stream.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
