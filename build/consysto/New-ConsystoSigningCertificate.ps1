#requires -Version 5.1
<#
.SYNOPSIS
    Сертификат подписи пакетов Consysto Files (CN=Consysto).

.DESCRIPTION
    Запускает владелец: пароль вводится в этом окне и нигде не сохраняется.

    Без параметров создаёт сертификат в хранилище текущего пользователя (им подписывает Build-ConsystoFiles.ps1) и кладёт рядом:
      ConsystoFiles.pfx — ключ под паролем. Хранить в резервной копии, НЕ в git.
      ConsystoFiles.cer — открытая часть, её доверяют ноутбуки при установке.

    С -ImportFrom возвращает ключ из .pfx на новый компьютер: пакеты должны подписываться одним и тем же ключом,
    иначе Windows примет обновление за другую программу.

.EXAMPLE
    .\New-ConsystoSigningCertificate.ps1
.EXAMPLE
    .\New-ConsystoSigningCertificate.ps1 -ImportFrom E:\Backup\ConsystoFiles.pfx
#>
param(
    [string]$OutputDirectory = (Join-Path $env:USERPROFILE '.consysto\signing'),
    [string]$ImportFrom,
    [int]$ValidYears = 10
)

$ErrorActionPreference = 'Stop'
$subject = 'CN=Consysto'

function Get-SigningCertificate {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

if ($ImportFrom) {
    $password = Read-Host 'Пароль ключа' -AsSecureString
    $imported = Import-PfxCertificate -FilePath $ImportFrom -CertStoreLocation Cert:\CurrentUser\My -Password $password
    Write-Host "Ключ импортирован: $($imported.Thumbprint), действует до $($imported.NotAfter.ToString('d'))."
    return
}

$existing = Get-SigningCertificate
if ($existing) {
    Write-Host "Сертификат уже есть: $($existing.Thumbprint), действует до $($existing.NotAfter.ToString('d'))."
    Write-Host 'Новый не создаю: все пакеты должны подписываться одним ключом.'
    return
}

$password = Read-Host 'Придумайте пароль для резервной копии ключа' -AsSecureString
$confirmation = Read-Host 'Повторите пароль' -AsSecureString
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))
$plainConfirmation = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirmation))
if ($plain.Length -lt 8 -or $plain -ne $plainConfirmation) {
    throw 'Пароли не совпадают или короче 8 символов. Ничего не создано.'
}

$certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $subject `
    -FriendlyName 'Consysto Files package signing' `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -CertStoreLocation Cert:\CurrentUser\My `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$pfx = Join-Path $OutputDirectory 'ConsystoFiles.pfx'
$cer = Join-Path $OutputDirectory 'ConsystoFiles.cer'
Export-PfxCertificate -Cert $certificate -FilePath $pfx -Password $password | Out-Null
Export-Certificate -Cert $certificate -FilePath $cer | Out-Null

Write-Host "Готово. Сертификат $($certificate.Thumbprint), действует до $($certificate.NotAfter.ToString('d'))."
Write-Host "Ключ с паролем: $pfx — положите в резервную копию."
Write-Host "Открытая часть: $cer"
