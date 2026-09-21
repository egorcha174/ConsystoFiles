# Builds Consysto.StepMesher.exe against the OpenCascade binaries in third_party and stages it, together with every
# DLL it actually loads and the license texts, into native\out\occt: the folder the WinUI library ships next to the host.
param(
    # The minimal build from build-occt.ps1: the official binaries load FreeType, FreeImage, ffmpeg and OpenVR at start-up.
    [string]$Occt = "$PSScriptRoot\..\..\third_party\occt-8.0.1-min",
    # Extra DLL folders to search (e.g. third-party runtimes of another OCCT build); none for the minimal build.
    [string[]]$ExtraDllFolders = @()
)

$ErrorActionPreference = 'Stop'
$Occt = (Resolve-Path $Occt).Path
# Install layouts differ between OCCT builds (win64\vc14\bin, bin, ...): locate the folders by their files.
$occtInclude = (Get-ChildItem $Occt -Recurse -Filter Standard.hxx | Select-Object -First 1).DirectoryName
$occtLib = (Get-ChildItem $Occt -Recurse -Filter TKernel.lib | Select-Object -First 1).DirectoryName
$occtBin = (Get-ChildItem $Occt -Recurse -Filter TKernel.dll | Select-Object -First 1).DirectoryName
if (-not ($occtInclude -and $occtLib -and $occtBin)) { throw "OpenCascade headers, libraries or DLLs were not found under $Occt." }
$obj = Join-Path $PSScriptRoot 'obj'
$out = Join-Path $PSScriptRoot '..\out\occt'
# Start from an empty stage: DLLs left over from another OCCT build would otherwise ship with the host.
if (Test-Path $out) { Get-ChildItem $out -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force -Path $obj, $out | Out-Null
$out = (Resolve-Path $out).Path

$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the C++ tools was not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$dumpbin = Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -Recurse -Filter dumpbin.exe | Where-Object FullName -like '*Hostx64\x64*' | Select-Object -First 1

# Only the toolkits the mesher calls; the linker names any that are missing.
$libraries = 'TKDESTEP', 'TKDEIGES', 'TKDE', 'TKXSBase', 'TKMesh', 'TKShHealing', 'TKTopAlgo', 'TKBRep',
    'TKGeomBase', 'TKG3d', 'TKG2d', 'TKMath', 'TKernel' | ForEach-Object { "$_.lib" }

# cmd quoting across PowerShell is fragile; a generated batch file keeps the command line readable.
$batch = Join-Path $obj 'build.cmd'
@"
@echo off
rem vcvars64 looks vswhere up on PATH and complains when it is not there.
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "$vcvars" >nul || exit /b 1
cl /nologo /std:c++17 /EHsc /O2 /MD /W3 /DNDEBUG /utf-8 /I"$occtInclude" /Fo"$obj\\" "$PSScriptRoot\main.cpp" ^
   /link /nologo /SUBSYSTEM:CONSOLE /LIBPATH:"$occtLib" $($libraries -join ' ') ^
   /IMPLIB:"$obj\Consysto.StepMesher.lib" /OUT:"$out\Consysto.StepMesher.exe"
"@ | Set-Content -Path $batch -Encoding ASCII

& cmd /c "`"$batch`""
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }

# Copy the DLL closure: follow the import tables from the exe through OCCT and its third-party DLLs.
# System and VC runtime DLLs are not found in these folders and stay out.
$searchFolders = @($occtBin) + $ExtraDllFolders
$pending = [System.Collections.Generic.Queue[string]]::new()
$pending.Enqueue((Join-Path $out 'Consysto.StepMesher.exe'))
$copied = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($pending.Count -gt 0) {
    $binary = $pending.Dequeue()
    $dependencies = & $dumpbin.FullName /nologo /dependents $binary | Where-Object { $_ -match '^\s+\S+\.dll\s*$' } | ForEach-Object { $_.Trim() }
    foreach ($name in $dependencies) {
        if ($copied.Contains($name)) { continue }
        $source = $searchFolders | ForEach-Object { Join-Path $_ $name } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $source) { continue }
        Copy-Item $source $out -Force
        [void]$copied.Add($name)
        $pending.Enqueue((Join-Path $out $name))
    }
}

# Import tables that point nowhere mean the mesher will not start on another machine: fail the build instead.
$unresolved = foreach ($binary in Get-ChildItem $out -Include *.exe, *.dll -Recurse) {
    & $dumpbin.FullName /nologo /dependents $binary.FullName | Where-Object { $_ -match '^\s+\S+\.dll\s*$' } | ForEach-Object { $_.Trim() } |
        Where-Object { $_ -notmatch '(?i)^api-ms-win-' -and -not (Test-Path (Join-Path $out $_)) -and -not (Test-Path (Join-Path $env:SystemRoot "System32\$_")) } |
        ForEach-Object { "$($binary.Name) -> $_" }
}
if ($unresolved) { throw "Unresolved DLL imports:`n$($unresolved -join "`n")" }

# LGPL 2.1 with the OCCT exception (and licenses of any extra runtimes) travel with the binaries.
$licenses = Join-Path $out 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Get-ChildItem $Occt -File -Recurse -Depth 1 | Where-Object { $_.Name -match '(?i)^(license|occt_lgpl_exception)' } | Copy-Item -Destination $licenses -Force
foreach ($folder in $ExtraDllFolders) {
    Get-ChildItem $folder -File | Where-Object { $_.Name -match '(?i)license|copying' } | Copy-Item -Destination $licenses -Force
}

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"staged {0} DLLs, {1:N1} MB in {2}" -f $copied.Count, $size, $out
