@echo off
chcp 65001 >nul
cd /d "%~dp0"
set PYTHONIOENCODING=utf-8
set PY=python
where python >nul 2>nul
if errorlevel 1 set PY=py
%PY% p5r_stats_live.py
echo.
pause
