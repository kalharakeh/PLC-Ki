@echo off
rem Starts PLC-Ki on this PC and opens it in the browser. Double-click it.
rem Keep the window open while you use PLC-Ki; close it to stop PLC-Ki.
title PLC-Ki
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo  The .NET 8 SDK is not installed on this PC.
  echo  Get it from https://dotnet.microsoft.com/download/dotnet/8.0 ^(x64^), then double-click this file again.
  echo.
  pause
  exit /b 1
)
echo.
echo   Starting PLC-Ki. The browser opens by itself when it is ready:  http://localhost:5080
echo   Keep this window open while you use PLC-Ki. Close it to stop PLC-Ki.
echo.
rem Opens the browser as soon as PLC-Ki answers (the first start builds it, which takes a minute)
start "" /min powershell -NoProfile -Command "for ($i = 0; $i -lt 180; $i++) { try { Invoke-WebRequest http://localhost:5080/login.html -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process http://localhost:5080; break } catch { Start-Sleep 1 } }"
dotnet run --project src\PlcKi.Server
echo.
echo   PLC-Ki stopped (see the messages above).
pause
