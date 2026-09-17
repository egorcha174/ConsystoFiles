#requires -Version 5.1
<#
.SYNOPSIS
    Устанавливает или обновляет Consysto Files из этой папки.

.DESCRIPTION
    1. Если компьютер ещё не доверяет сертификату Consysto, добавляет его в доверенные (один запрос прав администратора).
    2. Ставит пакет для текущего пользователя; более новая версия встаёт поверх, настройки сохраняются.
    3. Запускает Consysto Files.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

try {
    $package = Join-Path $here 'ConsystoFiles.msix'
    $certificatePath = Join-Path $here 'ConsystoFiles.cer'
    if (-not (Test-Path -LiteralPath $package)) { throw 'Рядом нет ConsystoFiles.msix. Скопируйте папку установки целиком.' }
    if (-not (Test-Path -LiteralPath $certificatePath)) { throw 'Рядом нет ConsystoFiles.cer. Скопируйте папку установки целиком.' }

    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certificatePath
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint }
    if (-not $trusted) {
        Write-Host 'Windows спросит разрешение: нужно добавить сертификат Consysto в доверенные.'
        $command = "Import-Certificate -FilePath '$($certificatePath.Replace("'", "''"))' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
        $elevated = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command
        if ($elevated.ExitCode -ne 0) { throw 'Сертификат не добавлен: без него Windows не установит программу.' }
    }

    # Установка идёт от имени того, кто запустил, а не администратора: программа появится у него в «Пуске»
    $architecture = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
    $dependencies = @(Get-ChildItem (Join-Path $here "Dependencies\$architecture") -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.appx', '.msix' } |
        ForEach-Object FullName)

    Write-Host 'Устанавливаю Consysto Files...'
    if ($dependencies.Count -gt 0) {
        Add-AppxPackage -Path $package -DependencyPath $dependencies -ForceApplicationShutdown
    } else {
        Add-AppxPackage -Path $package -ForceApplicationShutdown
    }

    $installed = Get-AppxPackage -Name 'ConsystoFiles' | Sort-Object Version -Descending | Select-Object -First 1
    $applicationId = (Get-AppxPackageManifest $installed).Package.Applications.Application.Id
    Write-Host "Готово: Consysto Files $($installed.Version)."
    Start-Process "shell:AppsFolder\$($installed.PackageFamilyName)!$applicationId"
}
catch {
    Write-Host ''
    Write-Host "Не получилось: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host 'Нажмите Enter, чтобы закрыть окно'
    exit 1
}
