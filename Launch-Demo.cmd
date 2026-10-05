@echo off
setlocal
set "demoExe=%~dp0src\Visualizer.App\bin\Release\net9.0-windows\Visualizer.App.exe"
if not exist "%demoExe%" (
    echo Release executable not found. Run this command from the repository root:
    echo powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release -Test
    pause
    exit /b 1
)
start "" /D "%~dp0" "%demoExe%"
