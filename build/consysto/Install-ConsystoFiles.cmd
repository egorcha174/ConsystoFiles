@echo off
rem Consysto Files: installs or updates the program from this folder. In a release this file is named after the program.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-ConsystoFiles.ps1"
