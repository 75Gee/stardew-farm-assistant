@echo off
chcp 65001 >nul
cd /d "%~dp0"
REM 绿条 PRBS 在线辨识（默认约 25s，Esc 提前结束；结果 bar_id_data.npz）
if exist "%LocalAppData%\Programs\Python\Python313\python.exe" (
  "%LocalAppData%\Programs\Python\Python313\python.exe" bar_system_id.py identify %*
) else if exist "%LocalAppData%\Programs\Python\Python312\python.exe" (
  "%LocalAppData%\Programs\Python\Python312\python.exe" bar_system_id.py identify %*
) else (
  python bar_system_id.py identify %*
)
if errorlevel 1 pause
