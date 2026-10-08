#requires -Version 5.1
function Move-BuildOutputToQuarantine([string]$Path, [string]$Boundary) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $root = [IO.Path]::GetFullPath($Boundary).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).ProviderPath)
    if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Путь результата сборки выходит за разрешённый каталог.'
    }
    # Old build output goes to the quarantine folder of the machine (D:\_trash_<date>), or the temp folder without a D: drive
    $quarantineBase = if (Test-Path -LiteralPath 'D:\') { 'D:\' } else { [IO.Path]::GetTempPath() }
    $quarantineRoot = [IO.Path]::GetFullPath((Join-Path $quarantineBase ('_trash_' + (Get-Date -Format yyyyMMdd))))
    $quarantine = [IO.Path]::GetFullPath((Join-Path $quarantineRoot ('Build-' + [Guid]::NewGuid().ToString('N') + '-' + [IO.Path]::GetFileName($target))))
    if (-not $quarantine.StartsWith($quarantineRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Недопустимый путь карантина сборки.'
    }
    New-Item -ItemType Directory -Force -Path $quarantineRoot | Out-Null
    Move-Item -LiteralPath $target -Destination $quarantine
}
