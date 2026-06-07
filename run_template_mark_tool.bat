@echo off
chcp 65001 >nul
cd /d "%~dp0"
if "%~1"=="" (
  echo 用法: 将截图拖到本 bat 上，或: run_template_mark_tool.bat 截图路径.png
  pause
  exit /b 1
)
if exist "%LocalAppData%\Programs\Python\Python313\python.exe" (
  "%LocalAppData%\Programs\Python\Python313\python.exe" template_mark_tool.py %*
) else if exist "%LocalAppData%\Programs\Python\Python312\python.exe" (
  "%LocalAppData%\Programs\Python\Python312\python.exe" template_mark_tool.py %*
) else (
  python template_mark_tool.py %*
)
