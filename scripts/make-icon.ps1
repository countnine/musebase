# 멀티사이즈 app.ico 생성 (16/24/32/48/64/128/256, PNG 압축 엔트리)
# 디자인: 녹색 원 + 흰색 "M" (트레이 런타임 아이콘과 동일)
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, $size * 0.04)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x1D, 0xB9, 0x54))
    $g.FillEllipse($brush, $pad, $pad, $size - 2 * $pad, $size - 2 * $pad)

    $fontSize = [single]($size * 0.55)
    $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, ($size * 0.03), $size, $size)
    $g.DrawString("M", $font, [System.Drawing.Brushes]::White, $rect, $format)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $size; Data = $ms.ToArray() }

    $format.Dispose(); $font.Dispose(); $brush.Dispose(); $g.Dispose(); $bmp.Dispose(); $ms.Dispose()
}

# ICO 컨테이너 조립
$outPath = Join-Path $PSScriptRoot "..\src\Musebase.Windows\assets\app.ico"
New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null
$stream = [System.IO.File]::Create($outPath)
$writer = New-Object System.IO.BinaryWriter($stream)

# ICONDIR
$writer.Write([uint16]0)               # reserved
$writer.Write([uint16]1)               # type: icon
$writer.Write([uint16]$pngs.Count)     # count

$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $writer.Write([byte]$dim)          # width
    $writer.Write([byte]$dim)          # height
    $writer.Write([byte]0)             # palette
    $writer.Write([byte]0)             # reserved
    $writer.Write([uint16]1)           # planes
    $writer.Write([uint16]32)          # bpp
    $writer.Write([uint32]$p.Data.Length)
    $writer.Write([uint32]$offset)
    $offset += $p.Data.Length
}
foreach ($p in $pngs) { $writer.Write($p.Data) }
$writer.Dispose(); $stream.Dispose()

Write-Host "생성됨: $outPath ($((Get-Item $outPath).Length) bytes)"
