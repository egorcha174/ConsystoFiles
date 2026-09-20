#requires -Version 5.1
<#
.SYNOPSIS
    Рисует иконку Consysto Files: папка со «шторкой» и молния Consysto поверх неё.

.DESCRIPTION
    Иконка своя: форма папки, цвета и наклон молнии нарисованы здесь, ничего не заимствуется из оформления Files —
    их логотип защищён как знак, а лицензия MPL распространяется только на код. Отсылка к исходному проекту читается
    в том, что это тоже папка в стиле Fluent, а принадлежность к Consysto — в молнии и фиолетовом цвете панели.

    Скрипт кладёт Logo.ico (16…256) и плитки Square44x44Logo / Square150x150Logo в наборы значков приложения.

.PARAMETER Preview
    Дополнительно сохранить PNG 512 для просмотра глазами, не трогая наборы значков.
#>
param(
    [string]$OutputDirectory,
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$filesRoot = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $filesRoot 'src\Files.App\Assets\AppTiles' }

# Цвета Consysto: молния из фирменного знака, папка — своя, приглушённая, чтобы молния читалась
$folderBack = [Drawing.Color]::FromArgb(255, 74, 92, 122)
$folderFront = [Drawing.Color]::FromArgb(255, 99, 124, 163)
$folderEdge = [Drawing.Color]::FromArgb(255, 58, 72, 97)
$bolt = [Drawing.Color]::FromArgb(255, 134, 59, 255)
$boltEdge = [Drawing.Color]::FromArgb(255, 237, 230, 255)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Icon([int]$size) {
    $bitmap = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([Drawing.Color]::Transparent)

    # Всё рисуется в долях размера, поэтому мелкие размеры выходят такими же, как крупные
    $u = $size / 100.0

    # Задняя половина папки с язычком
    $back = New-RoundedPath (10 * $u) (22 * $u) (80 * $u) (62 * $u) (9 * $u)
    $tab = New-RoundedPath (10 * $u) (16 * $u) (38 * $u) (20 * $u) (6 * $u)
    $backBrush = New-Object Drawing.SolidBrush($folderBack)
    $g.FillPath($backBrush, $tab)
    $g.FillPath($backBrush, $back)

    # Передняя стенка — чуть светлее, с тонкой кромкой сверху
    $front = New-RoundedPath (10 * $u) (34 * $u) (80 * $u) (50 * $u) (9 * $u)
    $frontBrush = New-Object Drawing.Drawing2D.LinearGradientBrush(
        (New-Object Drawing.PointF([single]0, [single](34 * $u))), (New-Object Drawing.PointF([single]0, [single](84 * $u))), $folderFront, $folderBack)
    $g.FillPath($frontBrush, $front)
    $edgePen = New-Object Drawing.Pen($folderEdge, [single](1.2 * $u))
    $g.DrawPath($edgePen, $front)

    # Молния Consysto: ломаная со сдвигом влево-вниз, как в знаке
    # На маленьких значках тонкая молния расплывается в пятно, поэтому там она шире
    $shape = if ($size -le 32) { @(60, 22, 29, 60, 49, 60, 40, 84, 73, 46, 51, 46) }
    else { @(61, 27, 36, 57, 53, 57, 40, 79, 66, 49, 48, 49) }
    $points = @()
    for ($i = 0; $i -lt $shape.Count; $i += 2) {
        $points += New-Object Drawing.PointF([single]($shape[$i] * $u), [single]($shape[$i + 1] * $u))
    }
    $boltPath = New-Object Drawing.Drawing2D.GraphicsPath
    $boltPath.AddPolygon($points)
    $boltPath.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush($bolt)), $boltPath)
    if ($size -ge 48) {
        $g.DrawPath((New-Object Drawing.Pen($boltEdge, [single](0.7 * $u))), $boltPath)
    }

    $g.Dispose()
    return $bitmap
}

function Save-Png([Drawing.Bitmap]$bitmap, [string]$path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
}

# Значок окна и панели задач: все размеры в одном файле, каждый нарисован отдельно
function Save-Ico([string]$path, [int[]]$sizes) {
    $streams = @()
    foreach ($size in $sizes) {
        $bitmap = New-Icon $size
        $stream = New-Object IO.MemoryStream
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        $streams += , $stream
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    $file = [IO.File]::Create($path)
    $writer = New-Object IO.BinaryWriter($file)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
        $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$streams[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $streams[$i].Length
    }
    foreach ($stream in $streams) {
        $writer.Write($stream.ToArray())
        $stream.Dispose()
    }
    $writer.Dispose()
    $file.Dispose()
}

foreach ($set in 'Dev', 'Preview', 'Release') {
    $directory = Join-Path $OutputDirectory $set
    if (-not (Test-Path -LiteralPath $directory)) { continue }

    Save-Ico (Join-Path $directory 'Logo.ico') @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

    foreach ($tile in @{ 'Square44x44Logo' = 44; 'Square150x150Logo' = 150; 'Small71x71Logo' = 71; 'Large310x310Logo' = 310 }.GetEnumerator()) {
        foreach ($scale in 100, 125, 150, 200, 400) {
            $pixels = [int][math]::Round($tile.Value * $scale / 100.0)
            $bitmap = New-Icon $pixels
            Save-Png $bitmap (Join-Path $directory "$($tile.Key).scale-$scale.png")
            $bitmap.Dispose()
        }
    }

    Write-Host "Готово: $directory"
}

if ($Preview) {
    $bitmap = New-Icon 512
    $path = Join-Path $env:TEMP 'consysto-files-icon.png'
    Save-Png $bitmap $path
    $bitmap.Dispose()
    Write-Host "Просмотр: $path"
}
