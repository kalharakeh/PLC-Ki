# Builds the PLC-Ki installer: out\PLC-Ki-Setup-<version>.exe
# Needs the .NET 8 SDK and Inno Setup 6 (https://jrsoftware.org/isdl.php, or: winget install JRSoftware.InnoSetup).
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "out\publish"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# Self-contained: the plant PC needs no .NET installed
dotnet publish (Join-Path $root "src\PlcKi.Server\PlcKi.Server.csproj") -c Release -r win-x64 --self-contained true `
  -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
  (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $PSScriptRoot "PlcKi.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "Installer: $(Join-Path $root "out\PLC-Ki-Setup-$Version.exe")"
