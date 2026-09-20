<#
    .SYNOPSIS
    Отправляет команду окну Consysto Files через канал управления.

    .EXAMPLE
    .\Send-FilesCommand.ps1 open -Path 'D:\Проекты'
    .\Send-FilesCommand.ps1 splitPanes -Path 'D:\Проекты\Гриль'
    .\Send-FilesCommand.ps1 state
#>
param(
    [Parameter(Mandatory)][string]$Command,
    [string]$Path,
    [string]$Where,
    [int]$Index,
    [string]$Name,
    [string[]]$Folders,
    [int]$TimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'

$request = @{ command = $Command }
if ($PSBoundParameters.ContainsKey('Path'))  { $request.path  = $Path }
if ($PSBoundParameters.ContainsKey('Where')) { $request.where = $Where }
if ($PSBoundParameters.ContainsKey('Index')) { $request.index = $Index }
if ($PSBoundParameters.ContainsKey('Name'))  { $request.name  = $Name }
if ($PSBoundParameters.ContainsKey('Folders')) { $request.folders = $Folders }

$pipe = New-Object IO.Pipes.NamedPipeClientStream '.', 'ConsystoFiles', 'InOut'
try {
    $pipe.Connect($TimeoutMs)
} catch {
    throw "Consysto Files не отвечает. Программа запущена? Управление включено в настройках?"
}

try {
    $writer = New-Object IO.StreamWriter $pipe, (New-Object Text.UTF8Encoding $false)
    $writer.AutoFlush = $true
    $reader = New-Object IO.StreamReader $pipe, ([Text.Encoding]::UTF8)

    $writer.WriteLine(($request | ConvertTo-Json -Compress))
    $answer = $reader.ReadLine() | ConvertFrom-Json
} finally {
    $pipe.Dispose()
}

if (-not $answer.ok) { throw $answer.error }
$answer
