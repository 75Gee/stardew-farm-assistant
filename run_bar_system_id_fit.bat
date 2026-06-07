@echo off
chcp 65001 >nul
cd /d "%~dp0"
REM 对已保存数据离线 OLS + Ackermann（默认读同目录 bar_id_data.npz）
if exist "%LocalAppData%\Programs\Python\Python313\python.exe" (
  "%LocalAppData%\Programs\Python\Python313\python.exe" bar_system_id.py fit --input "%~dp0bar_id_data.npz" %*
) else if exist "%LocalAppData%\Programs\Python\Python312\python.exe" (
  "%LocalAppData%\Programs\Python\Python312\python.exe" bar_system_id.py fit --input "%~dp0bar_id_data.npz" %*
) else (
  python bar_system_id.py fit --input "%~dp0bar_id_data.npz" %*
)
if errorlevel 1 pause
