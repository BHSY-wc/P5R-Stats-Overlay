@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "%~dp0P5RPanel\P5ROverlay.exe" (
    echo Panel not found: P5RPanel\P5ROverlay.exe
    pause
    exit /b 1
)
start "" "%~dp0P5RPanel\P5ROverlay.exe"
exit
