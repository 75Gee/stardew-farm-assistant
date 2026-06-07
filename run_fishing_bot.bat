@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "%LocalAppData%\Programs\Python\Python313\python.exe" (
  "%LocalAppData%\Programs\Python\Python313\python.exe" fishing_bot.py
) else if exist "%LocalAppData%\Programs\Python\Python312\python.exe" (
  "%LocalAppData%\Programs\Python\Python312\python.exe" fishing_bot.py
) else (
  python fishing_bot.py
)
if errorlevel 1 pause
