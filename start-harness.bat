@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0bin\DshTray.exe"

if exist "%EXE%" goto run

echo [start-harness] First run: building self-contained tray host (embedded whale icon)...
where powershell >nul 2>nul
if errorlevel 1 (
    echo Error: PowerShell not found. Install Windows PowerShell and retry.
    pause
    exit /b 1
)
if not exist "%~dp0assets\DeepSeekWhale.ico" (
    call powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-icon.ps1"
)
call powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-exe.ps1"
if errorlevel 1 (
    echo Error: build failed. See output above.
    pause
    exit /b 1
)

:run
start "" "%EXE%"
timeout /t 1 /nobreak >nul
exit /b 0
