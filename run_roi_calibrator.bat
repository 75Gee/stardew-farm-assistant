@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "%LocalAppData%\Programs\Python\Python313\python.exe" (
  "%LocalAppData%\Programs\Python\Python313\python.exe" roi_calibrator.py
) else if exist "%LocalAppData%\Programs\Python\Python312\python.exe" (
  "%LocalAppData%\Programs\Python\Python312\python.exe" roi_calibrator.py
) else (
  python roi_calibrator.py
)
if errorlevel 1 pause
