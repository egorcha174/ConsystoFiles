#requires -Version 5.1
<#
.SYNOPSIS
    Упаковывает готовую портативную сборку в установщик NSIS для текущего пользователя.
.PARAMETER PortableDir
    Корень ConsystoFiles-portable_* с подпапкой app. По умолчанию выбирается наибольшая версия.
.PARAMETER Version
    Четырёхкомпонентная версия; по умолчанию берётся из Files.exe.
.PARAMETER NsisPath
    Путь к makensis.exe. Также поддерживаются PATH и стандартная установка NSIS.
#>
param(
    [string]$PortableDir,
    [string]$Version,
    [string]$NsisPath
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifacts = Join-Path $repo 'artifacts\ConsystoFiles'
if (-not $PortableDir) {
    $latest = Get-ChildItem -LiteralPath $artifacts -Directory |
        Where-Object { $_.Name -match '^ConsystoFiles-portable_\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]($_.Name -replace '^ConsystoFiles-portable_', '') } -Descending |
        Select-Object -First 1
    if (-not $latest) { throw "Нет портативных сборок в $artifacts" }
    $PortableDir = $latest.FullName
}
$PortableDir = (Resolve-Path -LiteralPath $PortableDir).ProviderPath
$payload = Join-Path $PortableDir 'app'
$exe = Join-Path $payload 'Files.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Не найден $exe" }
$payloadVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if (-not $Version) { $Version = $payloadVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    @($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count) {
    throw 'Version должна содержать четыре числа от 0 до 65535.'
}
if ($Version -ne $payloadVersion) { throw "Версия $Version отличается от Files.exe ($payloadVersion)." }

if (-not $NsisPath) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $(if ($command) { $command.Source }),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe' }),
        (Join-Path (Split-Path $repo -Parent) 'Consysto\build\joint\.tools\nsis-3.12\makensis.exe')
    )
    $NsisPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
}
if (-not $NsisPath -or -not (Test-Path -LiteralPath $NsisPath -PathType Leaf)) {
    throw 'NSIS не найден. Укажите -NsisPath <путь к makensis.exe>.'
}
$NsisPath = (Resolve-Path -LiteralPath $NsisPath).ProviderPath

# Данные никогда не входят в поставку, даже если исходную копию уже запускали.
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force |
    Where-Object { $_.FullName.Substring($payload.Length + 1) -notmatch '(^|\\)data(\\|$)' })
$sizeKB = [long][Math]::Ceiling(($files | Measure-Object -Property Length -Sum).Sum / 1KB)
$output = Join-Path $artifacts "ConsystoFiles-setup_$Version.exe"

# Проверка только читает процессы; путь передаётся через окружение, без интерполяции в код.
# Коды: 10 — запущена установленная копия, 20 — проверка недостоверна.
$processCheck = @'
$ErrorActionPreference='Stop';try{foreach($p in Get-Process){if($p.ProcessName -eq 'Files'){$v=$p.Path;if(!$v){exit 20};if($v -ieq $env:CONSYSTO_SETUP_TARGET){exit 10}}};exit 0}catch{exit 20}
'@
$encodedCheck = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($processCheck))

# Временные файлы компилятора также остаются в artifacts, а не в профиле пользователя.
$scratch = Join-Path $artifacts '.setup-tmp'
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
try {
    $env:TEMP = $scratch
    $env:TMP = $scratch
    & $NsisPath /NOCONFIG /INPUTCHARSET UTF8 /WX /V3 "/DPAYLOAD=$payload" "/DOUTPUT=$output" `
        "/DVERSION=$Version" "/DESTIMATED_SIZE=$sizeKB" "/DPROCESS_CHECK=$encodedCheck" `
        (Join-Path $PSScriptRoot 'setup\ConsystoFiles-setup.nsi')
    if ($LASTEXITCODE -ne 0) { throw "makensis завершился с кодом $LASTEXITCODE." }
}
finally {
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
}
$result = Get-Item -LiteralPath $output
Write-Output "INSTALLER: $($result.FullName)"
Write-Output "SIZE: $($result.Length) bytes ($([Math]::Round($result.Length / 1MB, 2)) MiB)"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash)"
