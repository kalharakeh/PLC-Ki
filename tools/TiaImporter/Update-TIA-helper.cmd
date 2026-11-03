@echo off
rem PLC-Ki TIA helper: get the newest version, build it, approve it in TIA Openness, start it again.
rem Double-click it on the TIA PC or VM after PLC-Ki was updated (or the first time, to build the helper).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-TIA-helper.ps1"
echo.
pause
