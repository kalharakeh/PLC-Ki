@echo off
rem PLC-Ki TIA helper: runs "TiaImporter serve" so PLC-Ki on the PC can use TIA Portal here.
rem Starts by itself at log-in after Setup-TIA-helper. Double-click it if its window was closed.
setlocal
title PLC-Ki TIA helper
set "SETTINGS=%LOCALAPPDATA%\PLC-Ki\tia-helper-settings.cmd"
set "EXE=%~dp0bin\Release\net48\TiaImporter.exe"
if not exist "%SETTINGS%" (
  echo.
  echo  The TIA helper is not set up yet.
  echo  Double-click Setup-TIA-helper in this folder first.
  echo.
  pause
  exit /b 1
)
if not exist "%EXE%" (
  echo.
  echo  TiaImporter.exe was not found at:
  echo    %EXE%
  echo  Build it once, see README.md in this folder.
  echo.
  pause
  exit /b 1
)
call "%SETTINGS%"
set "ARGS=serve --token %PLCKI_TIA_TOKEN% --port %PLCKI_TIA_PORT%"
if defined PLCKI_TIA_PLCSIM set "ARGS=%ARGS% --plcsim-ip %PLCKI_TIA_PLCSIM%"
if defined PLCKI_TIA_ALLOW_REAL set "ARGS=%ARGS% --allow-real %PLCKI_TIA_ALLOW_REAL%"

:run
cls
color 1F
echo.
echo   ==============================================================
echo     PLC-Ki TIA helper
echo   ==============================================================
echo.
echo     PLC-Ki on the PC connects to this computer at:
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4"') do for /f "tokens=*" %%b in ("%%a") do echo        http://%%b:%PLCKI_TIA_PORT%
echo.
echo     Token:  %PLCKI_TIA_TOKEN%
if defined PLCKI_TIA_PLCSIM (echo     PLCSIM: %PLCKI_TIA_PLCSIM%) else (echo     PLCSIM: off)
if defined PLCKI_TIA_ALLOW_REAL (echo     Real PLC downloads: %PLCKI_TIA_ALLOW_REAL%, each confirmed in this window) else (echo     Real PLC downloads: off)
echo.
echo     Leave this window open. TIA opens in the background only when
echo     PLC-Ki sends work. Close the window to stop the helper.
echo   ==============================================================
echo.
"%EXE%" %ARGS%
echo.
echo   The helper stopped (see the message above).
echo   It starts again in 15 seconds. Close this window to stop it for good.
timeout /t 15
goto run
