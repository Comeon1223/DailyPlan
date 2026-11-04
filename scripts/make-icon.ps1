param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\DailyPlan.App\Assets\App.ico")
)

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
$dayGlyph = [char]0x65E5

function New-RoundedRectPath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [float]$Radius
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $Radius * 2
    $arc = New-Object System.Drawing.RectangleF($Rect.X, $Rect.Y, $diameter, $diameter)
    $path.AddArc($arc, 180, 90)
    $arc.X = $Rect.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $Rect.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $Rect.X
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()
    return $path
}

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $path = New-RoundedRectPath -Rect $rect -Radius ($size * 0.22)
    $startColor = [System.Drawing.Color]::FromArgb(255, 108, 99, 255)
    $endColor = [System.Drawing.Color]::FromArgb(255, 79, 70, 229)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $startColor, $endColor, 55.0)
    $graphics.FillPath($brush, $path)

    $fontSize = [Math]::Max(7, [float]($size * 0.47))
    $font = New-Object System.Drawing.Font("Microsoft YaHei", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center

    # Nudge the glyph up slightly so its visual centre matches the tile.
    $textRect = New-Object System.Drawing.RectangleF(0, -($size * 0.03), $size, $size)
    $graphics.DrawString($dayGlyph, $font, [System.Drawing.Brushes]::White, $textRect, $format)

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@{
        Size = $size
        Data = $stream.ToArray()
    }

    $format.Dispose()
    $font.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    $stream.Dispose()
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$fileStream = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter($fileStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $sizeByte = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$sizeByte)
    $writer.Write([byte]$sizeByte)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$image.Data.Length)
    $writer.Write([UInt32]$offset)
    $offset += $image.Data.Length
}

foreach ($image in $images) {
    $writer.Write($image.Data)
}

$writer.Flush()
$writer.Dispose()
$fileStream.Dispose()

Write-Output "Icon written to $OutputPath"
