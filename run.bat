@echo off
cd /d "%~dp0"

start "DaemonGuard Host" cmd /k dotnet run --project "src\DaemonGuard.Engine.Host\DaemonGuard.Engine.Host.csproj"
timeout /t 2 /nobreak >nul

echo Starting DaemonGuard UI (close the UI window to stop both)...
dotnet run --project "src\DaemonGuard.UI\DaemonGuard.UI.csproj" > "ui_error.log" 2>&1
if errorlevel 1 (
    echo.
    echo === UI exited with an error. Showing ui_error.log ===
    type "ui_error.log"
    pause
)

echo UI closed. Stopping DaemonGuard Host...
taskkill /IM "DaemonGuard.Engine.Host.exe" /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq DaemonGuard Host*" /F >nul 2>&1