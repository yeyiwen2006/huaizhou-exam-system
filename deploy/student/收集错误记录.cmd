@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-StudentDiagnostics.ps1"
echo 请把桌面“学生端错误记录”文件夹中的“本机存储状态.json”发回。
pause
