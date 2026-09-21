# Builds a minimal OpenCascade 8.0.1 for the STEP mesher: only the toolkits it calls plus their dependencies, without
# FreeType, FreeImage, ffmpeg, OpenVR, TBB, jemalloc, Tcl/Tk or OpenGL. The official Windows binaries load all of those
# at start-up through TKService, which STEP reading pulls in via TKXCAF, although the mesher never draws anything.
param(
    [string]$Source = "$PSScriptRoot\..\..\third_party\occt-8.0.1-src\OCCT-8.0.1",
    [string]$Install = "$PSScriptRoot\..\..\third_party\occt-8.0.1-min"
)

$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path $Source).Path
$buildDir = Join-Path $PSScriptRoot 'obj\occt-build'
New-Item -ItemType Directory -Force -Path $buildDir, $Install | Out-Null
$Install = (Resolve-Path $Install).Path

$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the C++ tools was not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$cmakeTools = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake'

# cmd quoting across PowerShell is fragile; a generated batch file keeps the command lines readable.
$batch = Join-Path $buildDir 'build-occt.cmd'
@"
@echo off
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;$cmakeTools\CMake\bin;$cmakeTools\Ninja;%PATH%"
call "$vcvars" >nul || exit /b 1
cmake -S "$Source" -B "$buildDir" -G Ninja ^
  -D CMAKE_BUILD_TYPE=Release ^
  -D INSTALL_DIR="$Install" ^
  -D BUILD_LIBRARY_TYPE=Shared ^
  -D BUILD_USE_PCH=ON ^
  -D BUILD_MODULE_ModelingData=OFF ^
  -D BUILD_MODULE_ModelingAlgorithms=OFF ^
  -D BUILD_MODULE_Visualization=OFF ^
  -D BUILD_MODULE_ApplicationFramework=OFF ^
  -D BUILD_MODULE_DataExchange=OFF ^
  -D BUILD_MODULE_Draw=OFF ^
  -D "BUILD_ADDITIONAL_TOOLKITS=TKDESTEP;TKDEIGES;TKMesh" ^
  -D USE_TK=OFF -D USE_FREETYPE=OFF -D USE_FREEIMAGE=OFF -D USE_FFMPEG=OFF -D USE_OPENVR=OFF ^
  -D USE_TBB=OFF -D USE_OPENGL=OFF -D USE_GLES2=OFF -D USE_D3D=OFF -D USE_VTK=OFF ^
  -D USE_RAPIDJSON=OFF -D USE_DRACO=OFF -D USE_EIGEN=OFF ^
  -D USE_MMGR_TYPE=NATIVE ^
  -D BUILD_DOC_Overview=OFF -D BUILD_DOC_RefMan=OFF || exit /b 2
cmake --build "$buildDir" --target install || exit /b 3
"@ | Set-Content -Path $batch -Encoding ASCII

$log = Join-Path $buildDir 'build.log'
$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath cmd.exe -ArgumentList "/c `"`"$batch`" > `"$log`" 2>&1`"" -PassThru -WindowStyle Hidden
# Egor keeps working meanwhile: compilers started after this inherit the below-normal priority class.
$process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
$process.WaitForExit()

Get-Content $log -Tail 25
"exit code $($process.ExitCode) after {0:N1} min" -f $watch.Elapsed.TotalMinutes
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
