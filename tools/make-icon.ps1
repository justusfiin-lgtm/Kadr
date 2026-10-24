# Рисует значок Kadr и собирает его в Kadr\Kadr.ico (все нужные Windows размеры).
# Запуск из папки E:\KADR:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
# Дизайн — в палитре плеера (CLAUDE.md, раздел 4.1): тёмный скруглённый квадрат цвета панели,
# синий треугольник «▶» акцентного цвета, под ним — толстая полоса перемотки (узнаваемая деталь Kadr).
# На маленьких размерах (до 24 px) полосы нет — только треугольник, иначе получится каша.
param([string]$Out = (Join-Path $PSScriptRoot '..\Kadr\Kadr.ico'), [string]$PreviewDir = '')

Add-Type -AssemblyName System.Drawing

$panel  = [System.Drawing.Color]::FromArgb(0x1E, 0x1F, 0x23)
$border = [System.Drawing.Color]::FromArgb(0x3F, 0x42, 0x4A)
$accent = [System.Drawing.Color]::FromArgb(0x4C, 0x9B, 0xFF)
$track  = [System.Drawing.Color]::FromArgb(0x3A, 0x3D, 0x45)
$white  = [System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0xFF)

function RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  $p
}

function Draw-Icon([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)

  $s = [single]$size
  $small = $size -le 24

  # Фон: скруглённый квадрат с тонкой рамкой.
  $margin = [single]([Math]::Max(0.5, $s * 0.04))
  $bg = RoundedRect $margin $margin ($s - 2 * $margin) ($s - 2 * $margin) ($s * 0.22)
  $g.FillPath((New-Object System.Drawing.SolidBrush $panel), $bg)
  # Рамка нужна и мелким размерам: иначе на тёмной панели задач плашка сливается с фоном.
  $pen = New-Object System.Drawing.Pen $border, ([single][Math]::Max(1, $s / 64))
  $g.DrawPath($pen, $bg)

  # Треугольник «▶» со скруглёнными углами (контур той же краской, концы круглые).
  if ($small) { $top = 0.25; $bottom = 0.75; $left = 0.36; $right = 0.76 }
  else        { $top = 0.20; $bottom = 0.62; $left = 0.38; $right = 0.72 }
  $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
  $tri.AddPolygon([System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF ($s * $left), ($s * $top)),
    (New-Object System.Drawing.PointF ($s * $right), ($s * ($top + $bottom) / 2)),
    (New-Object System.Drawing.PointF ($s * $left), ($s * $bottom))))
  $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $tri)
  $round = New-Object System.Drawing.Pen $accent, ([single]($s * 0.06))
  $round.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
  $g.DrawPath($round, $tri)

  # Полоса перемотки: дорожка, заполненная часть и белый бегунок — как на панели плеера.
  if (-not $small) {
    $barX = $s * 0.22; $barW = $s * 0.56; $barH = [single]([Math]::Max(2, $s * 0.075)); $barY = $s * 0.75
    $g.FillPath((New-Object System.Drawing.SolidBrush $track), (RoundedRect $barX $barY $barW $barH ($barH / 2)))
    $filled = $barW * 0.45
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), (RoundedRect $barX $barY $filled $barH ($barH / 2)))
    $thumb = [single]($barH * 1.7)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), ($barX + $filled - $thumb / 2), ($barY + $barH / 2 - $thumb / 2), $thumb, $thumb)
  }

  $g.Dispose()
  $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
  $bmp = Draw-Icon $size
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  if ($PreviewDir) { $bmp.Save((Join-Path $PreviewDir "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
  $bmp.Dispose()
  [pscustomobject]@{ Size = $size; Data = $ms.ToArray() }
}

# Файл .ico: заголовок, оглавление (по 16 байт на размер), затем сами картинки в формате PNG.
$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
  $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }   # 0 в оглавлении означает 256
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$img.Data.Length); $w.Write([uint32]$offset)
  $offset += $img.Data.Length
}
foreach ($img in $images) { $w.Write($img.Data) }
$w.Flush()
$target = [System.IO.Path]::GetFullPath($Out)
[System.IO.File]::WriteAllBytes($target, $ico.ToArray())
"Готово: $target ($($ico.Length) байт, размеры: $($sizes -join ', '))"
