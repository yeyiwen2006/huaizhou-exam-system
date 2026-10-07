@echo off
chcp 65001 >nul
cd /d "%~dp0"
python -X utf8 eng\development\sync_code.py %*
set "exam_sync_exit=%errorlevel%"
pause
exit /b %exam_sync_exit%
