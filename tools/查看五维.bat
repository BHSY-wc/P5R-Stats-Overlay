@echo off
chcp 65001 >nul
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
%PY% p5r_stats_save.py %*
echo.
pause
