@echo off
cd /d "%~dp0"
if not exist "publish\PercyAgent.exe" (
  echo Percy Agent has not been published yet.
  echo Run build.ps1 first.
  pause
  exit /b 1
)
start "Percy Agent" /min "publish\PercyAgent.exe" --open
