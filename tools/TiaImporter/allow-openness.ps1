# Pre-approves TiaImporter.exe for TIA Portal Openness, so TIA does not ask
# "Allow access?" (the Openness firewall prompt) every time the tool is rebuilt or updated.
#
# It writes the same registry entry TIA writes when you click "Yes to all" in that prompt:
#   HKLM\SOFTWARE\Siemens\Automation\Openness\<version>\Whitelist\TiaImporter.exe\Entry
#     Path          full path of the exe
#     DateModified  the exe's last-write time, UTC, "yyyy/MM/dd HH:mm:ss.fff"
#     FileHash      SHA-256 of the exe, Base64
# TIA checks all three, so run this again after every rebuild (the hash changes).
# Only approve an exe you built yourself from this repository.
#
# Run on the TIA PC (not needed on plant PCs). It asks for administrator rights.
#   powershell -ExecutionPolicy Bypass -File tools\TiaImporter\allow-openness.ps1
#   ... -Exe D:\tools\TiaImporter.exe -TiaVersion 20.0
param(
  [string]$Exe,
  [string]$TiaVersion = "20.0"
)
$ErrorActionPreference = "Stop"

if (-not $Exe) {
  # The newest TiaImporter.exe built in this folder
  $found = Get-ChildItem (Join-Path $PSScriptRoot "bin") -Recurse -Filter TiaImporter.exe -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $found) { throw "TiaImporter.exe not found under $PSScriptRoot\bin. Build it first (dotnet build -c Release) or pass -Exe." }
  $Exe = $found.FullName
}
$Exe = (Resolve-Path $Exe).Path

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
  [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  Write-Host "Asking for administrator rights..."
  $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Exe", "`"$Exe`"", "-TiaVersion", $TiaVersion)
  exit $p.ExitCode
}

$opennessKey = "HKLM:\SOFTWARE\Siemens\Automation\Openness\$TiaVersion"
if (-not (Test-Path $opennessKey)) {
  throw "TIA Portal Openness $TiaVersion is not installed on this PC ($opennessKey missing). Check -TiaVersion."
}

$file = Get-Item $Exe
$sha = [Security.Cryptography.SHA256]::Create()
$hash = [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($file.FullName)))
$modified = $file.LastWriteTimeUtc.ToString("yyyy'/'MM'/'dd HH:mm:ss.fff", [Globalization.CultureInfo]::InvariantCulture)

$key = "$opennessKey\Whitelist\$($file.Name)\Entry"
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name Path -Value $file.FullName
Set-ItemProperty -Path $key -Name DateModified -Value $modified
Set-ItemProperty -Path $key -Name FileHash -Value $hash

Write-Host "TIA Openness $TiaVersion now allows $($file.FullName) without asking."
Write-Host "Run this again after you rebuild or update TiaImporter.exe."
