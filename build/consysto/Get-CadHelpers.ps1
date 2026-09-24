#requires -Version 5.1
<#
.SYNOPSIS
    Кладёт рядом со сборкой помощника, который читает геометрию чужих CAD-форматов.

.DESCRIPTION
    Помощник — cadmpeg (Apache-2.0), отдельная программа, которую Consysto Files запускает
    в своём процессе только на время чтения файла. Он весит больше ста мегабайт, поэтому
    в репозитории не хранится: скрипт берёт официальный выпуск с GitHub, сверяет контрольную
    сумму и распаковывает.

    Скачанное остаётся в кеше, поэтому повторная сборка ничего не качает заново.

.PARAMETER Destination
    Куда положить cadmpeg.exe и его лицензию. По умолчанию — папка CadHelpers рядом со сборкой.

.PARAMETER Version
    Версия помощника. Менять только вместе с проверкой на настоящих файлах.
#>
param(
    [string]$Destination,
    [string]$Version = '0.6.0',
    [string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'Consysto\cad-helpers')
)

$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $Destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'CadHelpers'
}

$archiveName = 'cadmpeg-x86_64-pc-windows-msvc.zip'
$address = "https://github.com/cadmpeg/cadmpeg/releases/download/v$Version/$archiveName"
$cache = Join-Path $CacheDirectory $Version
$archive = Join-Path $cache $archiveName

New-Item -ItemType Directory -Force $cache | Out-Null

if (-not (Test-Path $archive)) {
    Write-Host "Скачиваю помощника cadmpeg $Version..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $address -OutFile $archive -UseBasicParsing
}

# Сумму публикуют рядом с архивом: скачанное сверяется с ней, а не принимается на веру.
# Отметка о сверке лежит рядом с архивом, и уже проверенный архив второй раз не сверяется:
# иначе сборка зависит от сети даже тогда, когда качать нечего, и падает от любого сбоя разрешения имён.
$checked = "$archive.sha256-ok"
if (-not (Test-Path $checked)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $published = (Invoke-WebRequest -Uri "$address.sha256" -UseBasicParsing).Content
    if ($published -is [byte[]]) { $published = [Text.Encoding]::ASCII.GetString($published) }
    $expected = "$published".Trim().Split()[0]
    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash

    if ($expected -and $actual -ne $expected) {
        Remove-Item $archive -Force
        throw "Контрольная сумма помощника не сошлась: ожидалась $expected, получена $actual. Файл удалён."
    }

    Set-Content -LiteralPath $checked -Value $actual -Encoding ascii
}

$unpacked = Join-Path $cache 'unpacked'
if (-not (Test-Path (Join-Path $unpacked 'cadmpeg.exe'))) {
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    Expand-Archive -LiteralPath $archive -DestinationPath $unpacked -Force
}

$exe = Get-ChildItem $unpacked -Recurse -Filter cadmpeg.exe | Select-Object -First 1
if (-not $exe) { throw "В архиве помощника нет cadmpeg.exe" }

New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item $exe.FullName (Join-Path $Destination 'cadmpeg.exe') -Force

foreach ($name in 'LICENSE', 'LICENSE-docs', 'README.md') {
    $file = Get-ChildItem $unpacked -Recurse -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($file) { Copy-Item $file.FullName (Join-Path $Destination $name) -Force }
}

$size = [math]::Round((Get-Item (Join-Path $Destination 'cadmpeg.exe')).Length / 1MB)
Write-Host "Помощник cadmpeg $Version на месте: $Destination ($size МБ)"
