#requires -Version 5.1
param(
    [string]$Destination,
    [string]$CacheDirectory = (Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\_cache'))) 'parasolid-runtime')
)
$ErrorActionPreference = 'Stop'
if (-not $Destination) { $Destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'Parasolid' }
$finalDestination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
if ([IO.Path]::GetFileName($finalDestination) -ne 'Parasolid') {
    throw 'Каталог помощника должен называться Parasolid.'
}
if ((Test-Path -LiteralPath $finalDestination) -and
    -not (Test-Path -LiteralPath (Join-Path $finalDestination 'parasolid-runtime.json'))) {
    throw 'Папка назначения не принадлежит помощнику Parasolid. Сборка остановлена.'
}
$Destination = $finalDestination + '.preparing-' + [Guid]::NewGuid().ToString('N')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'parasolid-runtime.json') -Raw | ConvertFrom-Json
$sources = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'parasolid-sources.json') -Raw | ConvertFrom-Json
$requiredLicences = @('parasolid-kit-MIT.txt', 'Apache-2.0.txt', 'OCCT-LGPL-2.1.txt',
    'OCCT-exception.txt', 'OCP-LICENSE.txt', 'pybind11-LICENSE.txt', 'delvewheel-LICENSE.txt',
    'rapidjson-LICENSE.txt', 'fmt-LICENSE.txt', 'freeimage-license-fi.txt.txt', 'freetype-FTL.TXT.txt',
    'imath-LICENSE.md.txt', 'jxrlib-LICENSE.txt', 'lcms2-LICENSE.txt', 'lerc-LICENSE.txt',
    'libdeflate-COPYING.txt', 'libjpeg-turbo-LICENSE.md.txt', 'liblzma-COPYING.txt',
    'liblzma-COPYING.0BSD.txt', 'libpng-LICENSE.txt', 'libraw-LICENSE.LGPL.txt',
    'libtiff-LICENSE.md.txt', 'libwebp-base-COPYING.txt', 'libzlib-LICENSE.txt',
    'openexr-LICENSE.md.txt', 'openjpeg-LICENSE.txt', 'openjph-LICENSE.txt', 'zstd-LICENSE.txt')
foreach ($name in $requiredLicences) {
    $licence = Join-Path $repo ('native\ParasolidMesher\licenses\' + $name)
    if (-not (Test-Path -LiteralPath $licence) -or (Get-Item -LiteralPath $licence).Length -eq 0) {
        throw "Отсутствует лицензия зависимости: $name. Сборка остановлена."
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path $CacheDirectory, $Destination | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-VerifiedAsset($asset) {
    $cached = Join-Path $CacheDirectory $asset.name
    if (-not (Test-Path -LiteralPath $cached)) {
        Invoke-WebRequest -Uri $asset.url -OutFile $cached -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -ne $asset.sha256) {
        throw "Контрольная сумма не совпала: $($asset.name). Сборка остановлена."
    }
    return $cached
}

function Expand-VerifiedZip([string]$archive, [string]$directory) {
    $root = [IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $root $entry.FullName))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Недопустимый путь в архиве runtime.'
            }
            if ($entry.FullName.EndsWith('/')) {
                New-Item -ItemType Directory -Force -Path $target | Out-Null
            } else {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        }
    } finally { $zip.Dispose() }
}

Expand-VerifiedZip (Get-VerifiedAsset $manifest.runtime) $Destination
foreach ($wheel in $manifest.wheels) {
    Expand-VerifiedZip (Get-VerifiedAsset $wheel) (Join-Path $Destination 'Lib\site-packages')
}
[IO.File]::WriteAllText((Join-Path $Destination 'python314._pth'), "python314.zip`r`n.`r`nLib\site-packages`r`n", [Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $repo 'native\ParasolidMesher\mesher.py') -Destination $Destination -Force
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Не найдены инструменты C++ Visual Studio.' }
& (Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe') (Join-Path $repo 'native\ParasolidMesher\ParasolidMesher.vcxproj') -p:Configuration=Release -p:Platform=x64 -v:quiet -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать помощника Parasolid.' }
Copy-Item -LiteralPath (Join-Path $repo 'native\ParasolidMesher\obj\out\Consysto.ParasolidMesher.exe') -Destination $Destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'parasolid-runtime.json') -Destination $Destination -Force
Copy-Item -LiteralPath (Join-Path $repo 'native\ParasolidMesher\NOTICE.md') -Destination $Destination -Force
Copy-Item -LiteralPath (Join-Path $repo 'native\ParasolidMesher\licenses') -Destination $Destination -Recurse -Force
$sourceDirectory = Join-Path $Destination 'Sources'
New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
foreach ($asset in $sources.sources) {
    Copy-Item -LiteralPath (Get-VerifiedAsset $asset) -Destination (Join-Path $sourceDirectory $asset.name)
}
foreach ($recipe in $sources.recipes) {
    $asset = [pscustomobject]@{name = [IO.Path]::GetFileName(([Uri]$recipe.url).AbsolutePath); url = $recipe.url; sha256 = $recipe.sha256}
    $archive = Get-VerifiedAsset $asset
    & (Join-Path $Destination 'python.exe') -I -S -B (Join-Path $PSScriptRoot 'Collect-ParasolidRecipes.py') $archive (Join-Path $sourceDirectory $recipe.package)
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось подготовить исходники зависимостей Parasolid.' }
}
Copy-Item -LiteralPath (Join-Path $repo 'native\ParasolidMesher\REBUILD.md') -Destination $sourceDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'parasolid-sources.json') -Destination $Destination
$dlls = Join-Path $Destination 'Lib\site-packages\cadquery_ocp_novtk.libs'
$inventoryPath = Join-Path $repo 'native\ParasolidMesher\dependency-inventory.json'
$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
if (@(Get-ChildItem -LiteralPath $dlls -Filter '*.dll' -File).Count -ne @($inventory.libraries).Count) {
    throw 'Состав DLL Parasolid отличается от проверенного перечня. Сборка остановлена.'
}
foreach ($library in $inventory.libraries) {
    $libraryPath = Join-Path $dlls $library.file
    if (-not (Test-Path -LiteralPath $libraryPath) -or
        (Get-FileHash -LiteralPath $libraryPath -Algorithm SHA256).Hash -ne $library.sha256) {
        throw 'DLL Parasolid отличается от проверенной версии. Сборка остановлена.'
    }
}
Copy-Item -LiteralPath $inventoryPath -Destination $Destination
foreach ($pattern in 'msvcp140-*.dll', 'vcomp140-*.dll') {
    $library = @(Get-ChildItem -LiteralPath $dlls -Filter $pattern -File)
    if ($library.Count -ne 1 -or (Get-AuthenticodeSignature -LiteralPath $library[0].FullName).Status -ne 'Valid') {
        throw 'Не подтверждена оригинальная подпись библиотеки Microsoft. Сборка остановлена.'
    }
}
& (Join-Path $Destination 'python.exe') -I -S -B -c 'import parasolid_kit, OCP'
if ($LASTEXITCODE -ne 0) { throw 'Автономный runtime Parasolid не загружается.' }
if (Test-Path -LiteralPath $finalDestination) {
    $quarantineRoot = [IO.Path]::GetFullPath(('D:\_trash_' + (Get-Date -Format yyyyMMdd)))
    $quarantine = [IO.Path]::GetFullPath((Join-Path $quarantineRoot ('ParasolidRuntime-' + [Guid]::NewGuid().ToString('N'))))
    if (-not $quarantine.StartsWith($quarantineRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Недопустимый путь карантина.'
    }
    New-Item -ItemType Directory -Force -Path $quarantineRoot | Out-Null
    Move-Item -LiteralPath $finalDestination -Destination $quarantine
}
Move-Item -LiteralPath $Destination -Destination $finalDestination
Write-Host "Помощник Parasolid подготовлен: $finalDestination"
