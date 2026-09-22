#requires -Version 5.1
<#
.SYNOPSIS
    Собирает обе сборки Consysto Files одной версией и обновляет установленную копию.

.DESCRIPTION
    Договорённость Егора от 22.09.2026: портативная и установленная сборки всегда идут
    вместе. Иначе выходит то, что уже было: правка есть в коде, лежит в портативной сборке,
    а проверяют её в установленной копии недельной давности — и час уходит на выяснение,
    почему «ничего не изменилось».

    Скрипт считает номер версии **один раз** и передаёт обеим сборкам, поэтому «О приложении»
    в обеих показывает одно и то же число. Собирать их порознь можно, но тогда номера
    разойдутся, и сравнивать станет нечего.

    Порядок: портативная сборка, затем установочный пакет, затем установка поверх текущей.
    Установка закрывает запущенную программу — так требует Windows при замене пакета.

.PARAMETER Version
    Задать номер вручную. По умолчанию — 1.<год>.<месяц день>.<час минута>.

.PARAMETER SkipInstall
    Собрать оба пакета, но не ставить. Пригодится, когда в программе открыта работа.

.PARAMETER PortableOnly
    Только портативная сборка. Нарушает договорённость — годится лишь для быстрой проверки,
    когда результат никто не будет открывать из «Пуска».
#>
param(
    [string]$Version,
    [switch]$SkipInstall,
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'

# Номер считается здесь и только здесь: в этом весь смысл скрипта
if (-not $Version) {
    $now = Get-Date
    $Version = '1.{0}.{1}.{2}' -f ($now.Year - 2000), ($now.Month * 100 + $now.Day), ($now.Hour * 100 + $now.Minute)
}

$here = $PSScriptRoot
$filesRoot = Split-Path (Split-Path $here -Parent) -Parent

Write-Host ''
Write-Host "Consysto Files $Version — портативная и установленная" -ForegroundColor Cyan
Write-Host ''

# 1. Портативная
& (Join-Path $here 'Build-ConsystoPortable.ps1') -Version $Version
if ($LASTEXITCODE) { throw 'Портативная сборка не собралась.' }

if ($PortableOnly) {
    Write-Host ''
    Write-Host 'Собрана только портативная: установленная копия осталась прежней версии.' -ForegroundColor Yellow
    Write-Host 'Проверять правки в «Пуске» сейчас нельзя — там старая программа.' -ForegroundColor Yellow
    return
}

# 2. Установочный пакет
& (Join-Path $here 'Build-ConsystoFiles.ps1') -Version $Version
if ($LASTEXITCODE) { throw 'Установочный пакет не собрался.' }

$release = Join-Path $filesRoot "artifacts\ConsystoFiles\ConsystoFiles_$Version"
if (-not (Test-Path -LiteralPath $release)) { throw "Папка установки не найдена: $release" }

if ($SkipInstall) {
    Write-Host ''
    Write-Host "Оба пакета собраны. Установить: $release\Установить Consysto Files.cmd" -ForegroundColor Yellow
    return
}

# 3. Установка поверх текущей
Write-Host ''
Write-Host 'Обновляю установленную копию (запущенная программа будет закрыта)...'
& (Join-Path $release 'Install-ConsystoFiles.ps1')

$installed = Get-AppxPackage -Name ConsystoFiles | Select-Object -First 1
if ($installed -and $installed.Version -eq $Version) {
    Write-Host ''
    Write-Host "Готово. Обе сборки версии $Version, установленная копия обновлена." -ForegroundColor Green
}
else {
    $what = if ($installed) { $installed.Version } else { 'не установлена' }
    throw "Установленная копия осталась версии $what, а собрано $Version. Проверьте вывод выше."
}
