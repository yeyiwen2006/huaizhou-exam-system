@echo off
chcp 65001 >nul
cd /d "%~dp0"
python -X utf8 eng\development\start_local.py %*
set "exam_local_exit=%errorlevel%"
if not "%exam_local_exit%"=="0" pause
exit /b %exam_local_exit%
