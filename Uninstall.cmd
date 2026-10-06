@rem Double-click to take the ADOFAI Visualizer off this computer; uninstall.ps1 shows everything and asks part by part.
@rem The work is all on the one line below and starts outside this folder: the folder (this file included) may be gone
@rem when PowerShell returns, so cmd must neither hold it open nor come back to read another line from this file;
@rem (goto) ends the batch first, or cmd would look for the vanished file on the way out.
@cd /d "%~dp0.." & powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %* & echo. & pause & (goto) 2>nul & exit /b
