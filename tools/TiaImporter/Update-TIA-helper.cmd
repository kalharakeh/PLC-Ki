@echo off
rem PLC-Ki TIA helper: get the newest version, build it, approve it in TIA Openness, start it again.
rem Double-click it on the TIA PC or VM after PLC-Ki was updated (or the first time, to build the helper).
rem Options are passed on, e.g. "Update-TIA-helper -NoPull" builds the folder as it is, without git pull.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-TIA-helper.ps1" %*
echo.
pause
