#requires -Version 5.1
<#
.SYNOPSIS
    Собирает портативную сборку Consysto Files: папка с Files.exe, без установки.

.DESCRIPTION
    Репозиторий не меняется: исходники копируются во временную папку, там сборке дают своё имя и тип сборки Consysto
    (без обновлений с files.community и без отчётов Sentry), затем собирается Release x64 с ключом ConsystoPortable.

    Портативная сборка держит все свои данные в папке data рядом с Files.exe и не пишет ни в реестр Windows,
    ни в профиль пользователя. Часть возможностей установленной версии в ней недоступна: замена Проводника,
    уведомления Windows и обновление через магазин.

    Результат — папка artifacts\ConsystoFiles\ConsystoFiles-portable_<версия> и такой же .zip рядом.

.PARAMETER SkipArchive
    Не упаковывать в .zip, оставить только папку (быстрее при проверках).

.PARAMETER Demo
    Демонстрационная сборка для скриншотов: имена дисков и компьютера условные (ключ ConsystoDemo).
    Кладётся в ConsystoFiles-demo_<версия>, собирается в своей временной папке и не раздаётся.
#>
param(
    [string]$Version,
    [string]$StagingDirectory,
    [string]$OutputDirectory,
    [switch]$SkipArchive,
    [switch]$Demo
)

if (-not $StagingDirectory) {
    $StagingDirectory = Join-Path $env:TEMP $(if ($Demo) { 'ConsystoFilesDemoBuild' } else { 'ConsystoFilesPortableBuild' })
}
$demoFlag = if ($Demo) { 'true' } else { 'false' }
$releaseKind = if ($Demo) { 'demo' } else { 'portable' }

$ErrorActionPreference = 'Stop'
$filesRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $filesRoot 'artifacts\ConsystoFiles' }

# Версия растёт со временем сборки: 1.<год>.<месяц день>.<час минута>, каждая часть не больше 65535
if (-not $Version) {
    $now = Get-Date
    $Version = '1.{0}.{1}.{2}' -f ($now.Year - 2000), ($now.Month * 100 + $now.Day), ($now.Hour * 100 + $now.Minute)
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild из Visual Studio не найден.' }

# Компоновщик Native AOT ищет vswhere.exe в PATH (на агентах CI он там есть)
$env:PATH = (Split-Path $vswhere) + ';' + $env:PATH

Write-Host "Consysto Files $Version (портативная)"

# 1. Копия исходников; bin и obj копии остаются между сборками, так следующая сборка быстрее
$stageFiles = Join-Path $StagingDirectory 'files'
New-Item -ItemType Directory -Force -Path $stageFiles | Out-Null
robocopy $filesRoot $stageFiles /MIR /XD bin obj .git .vs artifacts node_modules /XF *.pfx /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Не удалось скопировать исходники (robocopy $LASTEXITCODE)." }

$packages = Join-Path $filesRoot 'packages'
if (Test-Path -LiteralPath $packages) {
    robocopy $packages (Join-Path $stageFiles 'packages') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Не удалось скопировать пакеты NuGet (robocopy $LASTEXITCODE)." }
}

function Update-Text([string[]]$Include, [string]$From, [string]$To) {
    Get-ChildItem (Join-Path $stageFiles 'src') -Recurse -File -Include $Include |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $bytes = [IO.File]::ReadAllBytes($_.FullName)
            $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
            $text = [IO.File]::ReadAllText($_.FullName)
            if ($text.Contains($From)) {
                [IO.File]::WriteAllText($_.FullName, $text.Replace($From, $To), (New-Object Text.UTF8Encoding $hasBom))
            }
        }
}

Update-Text -Include '*.csproj', '*.appxmanifest', '*.xaml' -From 'Assets\AppTiles\Dev' -To 'Assets\AppTiles\Release'
Update-Text -Include '*.cs', '*.cpp' -From 'files-dev' -To 'consysto-files'
Update-Text -Include '*.cs' -From 'cd_app_env_placeholder' -To 'Consysto'

# 2. Сборка. Files.App.Server собирается из цели MSBuild без восстановления, поэтому решение восстанавливается заранее
& $msbuild (Join-Path $stageFiles 'Files.slnx') -t:Restore -p:Platform=x64 -p:Configuration=Release -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Пакеты NuGet не восстановились.' }

# Files.App.Server отдаёт приложению свой .winmd, поэтому собирается первым
& $msbuild (Join-Path $stageFiles 'src\Files.App.Server\Files.App.Server.csproj') -t:Build -p:Platform=x64 -p:Configuration=Release -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Вспомогательный процесс не собрался.' }

$build = Join-Path $StagingDirectory 'portable'
if (Test-Path -LiteralPath $build) { Remove-Item -LiteralPath $build -Recurse -Force }
New-Item -ItemType Directory -Force -Path $build | Out-Null

& $msbuild (Join-Path $stageFiles 'src\Files.App\Files.App.csproj') `
    -restore -t:Build -p:Platform=x64 -p:Configuration=Release -p:ConsystoPortable=true "-p:ConsystoDemo=$demoFlag" `
    "-p:OutDir=$build\" -p:RestorePackagesConfig=true `
    "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" `
    -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Приложение не собралось.' }
if (-not (Test-Path -LiteralPath (Join-Path $build 'Files.exe'))) { throw 'Files.exe не собрался.' }

# 3. Чистка: отладочные файлы и данные пробных запусков в раздачу не идут
Get-ChildItem $build -Recurse -File -Include '*.pdb', '*.lib', '*.exp', 'AppxManifest.xml' |
    ForEach-Object { [IO.File]::Delete($_.FullName) }
$data = Join-Path $build 'data'
if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }

# 4. Папка для раздачи
$release = Join-Path $OutputDirectory "ConsystoFiles-${releaseKind}_$Version"
if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Split-Path $release -Parent) | Out-Null
robocopy $build $release /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Не удалось собрать папку раздачи (robocopy $LASTEXITCODE)." }

foreach ($name in 'README.md', 'README.ru.md', 'LICENSE-MIT', 'LICENSE-MPL', 'NOTICE.md') {
    $source = Join-Path $filesRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $release -Force }
}

# Помощник для чтения чужих CAD-форматов: в репозитории его нет, скрипт берёт официальный выпуск
& (Join-Path $PSScriptRoot 'Get-CadHelpers.ps1') -Destination (Join-Path $release 'CadHelpers')

# Движок OpenCascade: им читаются STEP и IGES. Собирается отдельно (native\StepMesher\build.ps1),
# потому что требует исходников OpenCascade; без него эти форматы просто не показываются.
$occtSource = Join-Path $filesRoot 'native\out\occt'
if (Test-Path -LiteralPath $occtSource) {
    $occtTarget = Join-Path $release 'occt'
    robocopy $occtSource $occtTarget /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Не удалось скопировать движок STEP (robocopy $LASTEXITCODE)." }
    $occtSize = [math]::Round((Get-ChildItem $occtTarget -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    Write-Host "Движок STEP и IGES на месте: $occtTarget ($occtSize МБ)"
} else {
    Write-Warning "Движка STEP нет ($occtSource) — в этой сборке STEP и IGES показываться не будут. Соберите native\StepMesher\build.ps1."
}

$size = [math]::Round((Get-ChildItem $release -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Папка: $release ($size МБ)"

if (-not $SkipArchive) {
    $archive = "$release.zip"
    if (Test-Path -LiteralPath $archive) { [IO.File]::Delete($archive) }
    # Пакуется сама папка, а не её содержимое: распаковка «сюда» не рассыпает пятьсот файлов по чужой папке
    Compress-Archive -LiteralPath $release -DestinationPath $archive -CompressionLevel Optimal
    $archiveSize = [math]::Round((Get-Item -LiteralPath $archive).Length / 1MB)
    Write-Host "Архив: $archive ($archiveSize МБ)"
}

exit 0

