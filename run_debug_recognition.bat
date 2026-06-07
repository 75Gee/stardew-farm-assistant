@echo off
chcp 65001 >nul
cd /d "%~dp0"
if "%~1"=="" (
  echo 用法: run_debug_recognition.bat 图片路径.png
  echo 整屏截图: run_debug_recognition.bat 整屏.png --full-frame
  pause
  exit /b 1
)
if exist "%LocalAppData%\Programs\Python\Python313\python.exe" (
  "%LocalAppData%\Programs\Python\Python313\python.exe" debug_recognition.py %*
) else if exist "%LocalAppData%\Programs\Python\Python312\python.exe" (
  "%LocalAppData%\Programs\Python\Python312\python.exe" debug_recognition.py %*
) else (
  python debug_recognition.py %*
)
pause
