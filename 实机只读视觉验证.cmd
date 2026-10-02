@echo off
setlocal EnableExtensions
chcp 65001 >nul

cd /d "%~dp0"
set "DIAG=%CD%\tools\TlbbAgent.Diagnostics\bin\Release\net8.0-windows\TlbbAgent.Diagnostics.exe"
set "RUN=%CD%\artifacts\live-userdesktop-dispatch"

if not exist "%DIAG%" (
  echo [FAIL] Diagnostics executable not found:
  echo %DIAG%
  pause
  exit /b 2
)

if not exist "%RUN%" mkdir "%RUN%"

echo started=%DATE% %TIME%>"%RUN%\run-status.txt"
echo safety=read-only; no keyboard; no mouse; no memory write>>"%RUN%\run-status.txt"

echo [1/5] Window binding audit...
"%DIAG%" window-binding >"%RUN%\01-window-binding.log" 2>&1
set "E1=%ERRORLEVEL%"

echo [2/5] Live full-frame recognition...
"%DIAG%" live-recognition >"%RUN%\02-live-recognition.log" 2>&1
set "E2=%ERRORLEVEL%"

echo [3/5] Live target-protection recognition...
"%DIAG%" live-target-protection >"%RUN%\03-live-target-protection.log" 2>&1
set "E3=%ERRORLEVEL%"

echo [4/5] Runtime snapshot...
"%DIAG%" runtime-snapshot >"%RUN%\04-runtime-snapshot.log" 2>&1
set "E4=%ERRORLEVEL%"

echo [5/5] Normal-hang read-only action preview...
"%DIAG%" live-normal-hang-preview --duration-ms 800 >"%RUN%\05-live-normal-hang-preview.log" 2>&1
set "E5=%ERRORLEVEL%"

(
  echo completed=%DATE% %TIME%
  echo window_binding=%E1%
  echo live_recognition=%E2%
  echo target_protection=%E3%
  echo runtime_snapshot=%E4%
  echo hang_preview=%E5%
  echo safety=read-only; no keyboard; no mouse; no memory write
)>>"%RUN%\run-status.txt"

echo.
echo Read-only verification finished.
echo Evidence: %RUN%
echo Exit codes: %E1%, %E2%, %E3%, %E4%, %E5%
echo.
pause
exit /b 0
