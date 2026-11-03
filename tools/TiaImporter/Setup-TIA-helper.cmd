@echo off
rem PLC-Ki TIA helper: one-time setup on the TIA PC or VM. Double-click this file.
rem It shows a small window, opens port 5090 (Windows asks for administrator rights once),
rem and makes the helper start by itself when you log in.
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Setup-TIA-helper.ps1"
