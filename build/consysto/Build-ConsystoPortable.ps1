#requires -Version 5.1
<#
.SYNOPSIS
    Собирает портативную сборку Consysto Files: приложение в app, запуск через .cmd, без установки.

.DESCRIPTION
    Репозиторий не меняется: исходники копируются во временную папку, там сборке дают своё имя и тип сборки Consysto
    (без обновлений с files.community и без отчётов Sentry), затем собирается Release x64 с ключом ConsystoPortable.

    Портативная сборка держит все свои данные в папке app\data рядом с Files.exe и не пишет ни в реестр Windows,
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
    [switch]$Demo,
    # Parasolid (X_T/X_B) — только по явному ключу: помощник весит ~240 МБ и настоящие X_T пока не читает (07.10.2026)
    [switch]$WithParasolid
)

if (-not $StagingDirectory) {
    $StagingDirectory = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\_build'))) $(if ($Demo) { 'ConsystoFilesDemoBuild' } else { 'ConsystoFilesPortableBuild' })
}
$demoFlag = if ($Demo) { 'true' } else { 'false' }
$releaseKind = if ($Demo) { 'demo' } else { 'portable' }

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Quarantine.ps1')
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

if ($WithParasolid) { & (Join-Path $PSScriptRoot 'Get-ParasolidHelpers.ps1') -Destination (Join-Path $stageFiles 'build\Parasolid') }

# 2. Сборка. Files.App.Server собирается из цели MSBuild без восстановления, поэтому решение восстанавливается заранее
& $msbuild (Join-Path $stageFiles 'Files.slnx') -t:Restore -p:Platform=x64 -p:Configuration=Release -v:quiet -clp:ErrorsOnly -nologo
if ($LASTEXITCODE -ne 0) { throw 'Пакеты NuGet не восстановились.' }

# Files.App.Server отдаёт приложению свой .winmd, поэтому собирается первым
& $msbuild (Join-Path $stageFiles 'src\Files.App.Server\Files.App.Server.csproj') -t:Build -p:Platform=x64 -p:Configuration=Release -v:quiet -clp:ErrorsOnly -nologo
if ($LASTEXITCODE -ne 0) { throw 'Вспомогательный процесс не собрался.' }

$build = Join-Path $StagingDirectory 'portable'
Move-BuildOutputToQuarantine $build $StagingDirectory
New-Item -ItemType Directory -Force -Path $build | Out-Null

& $msbuild (Join-Path $stageFiles 'src\Files.App\Files.App.csproj') `
    -restore -t:Build -p:Platform=x64 -p:Configuration=Release -p:ConsystoPortable=true "-p:ConsystoDemo=$demoFlag" `
    "-p:ConsystoParasolid=$(if ($WithParasolid) { 'true' } else { 'false' })" `
    "-p:OutDir=$build\" -p:RestorePackagesConfig=true `
    "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" `
    -v:quiet -clp:ErrorsOnly -nologo
if ($LASTEXITCODE -ne 0) { throw 'Приложение не собралось.' }
if (-not (Test-Path -LiteralPath (Join-Path $build 'Files.exe'))) { throw 'Files.exe не собрался.' }

# 3. Чистка: отладочные файлы и данные пробных запусков в раздачу не идут
Get-ChildItem $build -Recurse -File -Include '*.pdb', '*.lib', '*.exp', 'AppxManifest.xml' |
    ForEach-Object { [IO.File]::Delete($_.FullName) }
$data = Join-Path $build 'data'
Move-BuildOutputToQuarantine $data $StagingDirectory

# 4. Папка для раздачи
$release = Join-Path $OutputDirectory "ConsystoFiles-${releaseKind}_$Version"
Move-BuildOutputToQuarantine $release $OutputDirectory
New-Item -ItemType Directory -Force -Path (Split-Path $release -Parent) | Out-Null
$app = Join-Path $release 'app'
robocopy $build $app /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Не удалось собрать папку раздачи (robocopy $LASTEXITCODE)." }

$documents = 'README.md', 'README.ru.md', 'LICENSE-MIT', 'LICENSE-MPL', 'NOTICE.md'
foreach ($name in $documents) {
    $source = Join-Path $filesRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $release -Force }
}

$launcherName = 'Запустить Consysto Files.cmd'
$instructionsName = 'КАК ЗАПУСТИТЬ - HOW TO START.txt'
[IO.File]::WriteAllText((Join-Path $release $launcherName), '@start "" "%~dp0app\Files.exe" %*' + "`r`n", [Text.Encoding]::ASCII)
$instructions = @'
РУССКИЙ
1. До распаковки: правый клик по ZIP → Свойства → «Разблокировать» (если есть) → OK. Иначе Windows помечает файлы как скачанные из интернета.
2. Распакуйте архив в любую папку, например C:\Programs\Consysto Files. Не запускайте программу прямо из архива.
3. Запустите «Запустить Consysto Files.cmd». Если появится синее окно «Система Windows защитила ваш компьютер»: «Подробнее» → «Выполнить в любом случае». Программа бесплатная, исходники: https://github.com/egorcha174/ConsystoFiles
4. Для удобства: правый клик по app\Files.exe → Отправить → Рабочий стол (создать ярлык).
5. Вопросы и отзывы: https://github.com/egorcha174/ConsystoFiles/issues и https://t.me/print3d_lasercut
Обновление: закройте программу и сохраните папку app\data. При переходе со старой раскладки скопируйте прежнюю папку data в app\data новой сборки до первого запуска.

ENGLISH
1. Before extracting: right-click the ZIP → Properties → Unblock (if shown) → OK. Otherwise Windows marks the extracted files as downloaded from the internet.
2. Extract the archive to any folder, for example C:\Programs\Consysto Files. Do not run the program directly from the archive.
3. Run "Запустить Consysto Files.cmd". If the blue "Windows protected your PC" window appears: "More info" → "Run anyway". The program is free; source code: https://github.com/egorcha174/ConsystoFiles
4. For convenience: right-click app\Files.exe → Send to → Desktop (create shortcut).
5. Questions and feedback: https://github.com/egorcha174/ConsystoFiles/issues and https://t.me/print3d_lasercut
Updating: close the program and keep app\data. When upgrading from the old layout, copy the old data folder to app\data in the new build before the first launch.
'@
[IO.File]::WriteAllText((Join-Path $release $instructionsName), ($instructions -replace '\r?\n', "`r`n") + "`r`n", (New-Object Text.UTF8Encoding $true))

# Помощник для чтения чужих CAD-форматов: в репозитории его нет, скрипт берёт официальный выпуск
& (Join-Path $PSScriptRoot 'Get-CadHelpers.ps1') -Destination (Join-Path $app 'CadHelpers')

# Движок OpenCascade: им читаются STEP и IGES. Собирается отдельно (native\StepMesher\build.ps1),
# потому что требует исходников OpenCascade; без него эти форматы просто не показываются.
$occtSource = Join-Path $filesRoot 'native\out\occt'
if (Test-Path -LiteralPath $occtSource) {
    $occtTarget = Join-Path $app 'occt'
    robocopy $occtSource $occtTarget /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Не удалось скопировать движок STEP (robocopy $LASTEXITCODE)." }
    $occtSize = [math]::Round((Get-ChildItem $occtTarget -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    Write-Host "Движок STEP и IGES на месте: $occtTarget ($occtSize МБ)"
} else {
    Write-Warning "Движка STEP нет ($occtSource) — в этой сборке STEP и IGES показываться не будут. Соберите native\StepMesher\build.ps1."
}

if (-not (Test-Path -LiteralPath (Join-Path $app 'Files.exe') -PathType Leaf)) { throw 'В папке раздачи нет app\Files.exe.' }
$allowedRootItems = @('app', $launcherName, $instructionsName) + $documents
$unexpected = @(Get-ChildItem -LiteralPath $release -Force | Where-Object { $_.Name -notin $allowedRootItems })
if ($unexpected.Count -gt 0) { throw "Лишние файлы в корне раздачи: $($unexpected.Name -join ', ')" }

$size = [math]::Round((Get-ChildItem $release -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Папка: $release ($size МБ)"
Write-Host "Запуск: $(Join-Path $release $launcherName)"

if (-not $SkipArchive) {
    $archive = "$release.zip"
    Move-BuildOutputToQuarantine $archive $OutputDirectory
    # Пакуется сама папка, а не её содержимое: распаковка «сюда» не рассыпает пятьсот файлов по чужой папке
    Compress-Archive -LiteralPath $release -DestinationPath $archive -CompressionLevel Optimal
    $archiveSize = [math]::Round((Get-Item -LiteralPath $archive).Length / 1MB)
    Write-Host "Архив: $archive ($archiveSize МБ)"
}

exit 0

