@echo off
chcp 65001 >nul
mode con cols=92 lines=44 >nul 2>nul
cd /d "%~dp0"
set PYTHONIOENCODING=utf-8
set PY=python
where python >nul 2>nul
if errorlevel 1 set PY=py
%PY% -c "import Crypto" >nul 2>nul
if errorlevel 1 (
    echo Installing pycryptodome ...
    %PY% -m pip install pycryptodome -q
)
title P5R Status Panel
%PY% p5r_panel.py %*
echo.
pause
