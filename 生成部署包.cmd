@echo off
chcp 65001 >nul
cd /d "%~dp0"
where pwsh >nul 2>nul
if errorlevel 1 (
  echo Please install PowerShell 7, then run this entry again.
  pause
  exit /b 1
)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy\Publish-Deployment.ps1" %*
set "publishResult=%errorlevel%"
pause
exit /b %publishResult%
