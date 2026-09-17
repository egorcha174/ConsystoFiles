#requires -Version 5.1
<#
.SYNOPSIS
    Собирает установочный пакет Consysto Files для других компьютеров.

.DESCRIPTION
    Репозиторий не меняется: исходники копируются во временную папку, там пакету дают своё имя (ConsystoFiles, CN=Consysto),
    протокол consysto-files и тип сборки Consysto (без обновлений с files.community и без отчётов Sentry), затем
    собираются Release x64 и подписываются сертификатом из New-ConsystoSigningCertificate.ps1.

    Результат — папка artifacts\ConsystoFiles\ConsystoFiles_<версия> в репозитории files:
      ConsystoFiles.msix, Dependencies\, ConsystoFiles.cer, Install-ConsystoFiles.ps1 и «Установить Consysto Files.cmd».
    Её целиком копируют на ноутбук и запускают .cmd.

.PARAMETER Unsigned
    Только проверить, что пакет собирается. Неподписанный пакет не установить.
#>
param(
    [string]$Version,
    [string]$StagingDirectory = (Join-Path $env:TEMP 'ConsystoFilesBuild'),
    [string]$OutputDirectory,
    [switch]$Unsigned
)

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

$certificate = $null
if (-not $Unsigned) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq 'CN=Consysto' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if (-not $certificate) { throw 'Нет сертификата CN=Consysto. Сначала запустите New-ConsystoSigningCertificate.ps1.' }
}

Write-Host "Consysto Files $Version"

# 1. Копия исходников; bin и obj копии остаются между сборками, так следующая сборка быстрее
$stageFiles = Join-Path $StagingDirectory 'files'
New-Item -ItemType Directory -Force -Path $stageFiles | Out-Null
robocopy $filesRoot $stageFiles /MIR /XD bin obj .git .vs artifacts node_modules /XF *.pfx /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Не удалось скопировать исходники (robocopy $LASTEXITCODE)." }

# NuGet-пакеты C++ держат свои инструменты в папках bin, которые исключены выше: их копируем целиком
$packages = Join-Path $filesRoot 'packages'
if (Test-Path -LiteralPath $packages) {
    robocopy $packages (Join-Path $stageFiles 'packages') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Не удалось скопировать пакеты NuGet (robocopy $LASTEXITCODE)." }
}

# 2. Своя «личность» пакета
$manifestPath = Join-Path $stageFiles 'src\Files.App\Package.appxmanifest'
$manifest = New-Object System.Xml.XmlDocument
$manifest.PreserveWhitespace = $true
$manifest.Load($manifestPath)
$ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$ns.AddNamespace('pkg', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$ns.AddNamespace('uap5', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5')

$manifest.Package.Identity.Name = 'ConsystoFiles'
$manifest.Package.Identity.Publisher = 'CN=Consysto'
$manifest.Package.Identity.Version = $Version
$manifest.Package.Properties.DisplayName = 'Consysto Files'
$manifest.Package.Properties.PublisherDisplayName = 'Consysto'
$manifest.Package.Applications.Application.VisualElements.DisplayName = 'Consysto Files'
$manifest.Package.Applications.Application.VisualElements.DefaultTile.ShortName = 'Consysto Files'
$manifest.SelectSingleNode("/pkg:Package/pkg:Applications/pkg:Application/pkg:Extensions/uap:Extension[@Category='windows.protocol']/uap:Protocol", $ns).SetAttribute('Name', 'consysto-files')
$manifest.SelectSingleNode("/pkg:Package/pkg:Applications/pkg:Application/pkg:Extensions/uap5:Extension[@Category='windows.appExecutionAlias']/uap5:AppExecutionAlias/uap5:ExecutionAlias", $ns).SetAttribute('Alias', 'consysto-files.exe')
$manifest.Save($manifestPath)

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
foreach ($tiles in 'Dev', 'Preview') {
    $path = Join-Path $stageFiles "src\Files.App\Assets\AppTiles\$tiles"
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

# 3. Сборка: запускатель (C++), затем приложение с пакетом
& $msbuild (Join-Path $stageFiles 'src\Files.App.Launcher\Files.App.Launcher.vcxproj') -restore -t:Build -p:Platform=x64 -p:Configuration=Release -p:RestorePackagesConfig=true -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Запускатель не собрался.' }

# Внутренние проекты (Files.App.Server) собираются из цели MSBuild без восстановления, поэтому решение восстанавливается заранее, как в CI
& $msbuild (Join-Path $stageFiles 'Files.slnx') -t:Restore -p:Platform=x64 -p:Configuration=Release -p:PublishReadyToRun=true -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Пакеты NuGet не восстановились.' }

$packageDirectory = Join-Path $StagingDirectory 'AppxPackages'
if (Test-Path -LiteralPath $packageDirectory) { Remove-Item -LiteralPath $packageDirectory -Recurse -Force }
$arguments = @(
    (Join-Path $stageFiles 'src\Files.App\Files.App.csproj'),
    '-restore', '-t:Build',
    '-p:Platform=x64', '-p:Configuration=Release',
    "-p:AppxPackageDir=$packageDirectory\",
    '-p:AppxBundle=Never', '-p:GenerateAppxPackageOnBuild=true', '-p:UapAppxPackageBuildMode=SideloadOnly',
    '-p:RestorePackagesConfig=true', '-v:minimal', '-nologo'
)
if ($Unsigned) {
    $arguments += '-p:AppxPackageSigningEnabled=false'
} else {
    $arguments += '-p:AppxPackageSigningEnabled=true', "-p:PackageCertificateThumbprint=$($certificate.Thumbprint)"
}
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) { throw 'Приложение не собралось.' }

# 4. Папка для переноса на ноутбук
$msix = Get-ChildItem $packageDirectory -Recurse -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw 'Пакет .msix не найден.' }

$release = Join-Path $OutputDirectory "ConsystoFiles_$Version"
New-Item -ItemType Directory -Force -Path $release | Out-Null
Copy-Item -LiteralPath $msix.FullName -Destination (Join-Path $release 'ConsystoFiles.msix') -Force
$dependencies = Get-ChildItem $msix.DirectoryName -Directory -Filter 'Dependencies' | Select-Object -First 1
# Пакет собран под x64, поэтому среда Windows App SDK для других процессоров не нужна
$runtime = if ($dependencies) { Join-Path $dependencies.FullName 'x64' }
if ($runtime -and (Test-Path -LiteralPath $runtime)) {
    $target = Join-Path $release 'Dependencies'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath $runtime -Destination $target -Recurse -Force
}
if ($certificate) { Export-Certificate -Cert $certificate -FilePath (Join-Path $release 'ConsystoFiles.cer') | Out-Null }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-ConsystoFiles.ps1') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-ConsystoFiles.cmd') -Destination (Join-Path $release 'Установить Consysto Files.cmd') -Force

Write-Host "Готово: $release"
if ($Unsigned) { Write-Host 'Пакет не подписан: он только проверяет сборку, установить его нельзя.' }
