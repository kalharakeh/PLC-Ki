# PLC-Ki TIA helper update (on the TIA PC or VM). Started by Update-TIA-helper.cmd, or by Setup-TIA-helper.
#   1. stops the running helper (the build replaces its exe)
#   2. git pull in the PLC-Ki folder (skipped with -NoPull or without git)
#   3. builds TiaImporter (dotnet build -c Release)
#   4. approves the new exe in TIA Openness (allow-openness.ps1; Windows asks for administrator rights)
#   5. checks that this Windows user is in "Siemens TIA Openness", and offers to add it
#   6. starts the helper again (skipped with -NoStart)
param([switch]$NoPull, [switch]$NoStart)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here "..\..")).Path
$exe = Join-Path $here "bin\Release\net48\TiaImporter.exe"

function Step($n, $text) { Write-Host ""; Write-Host " $n. $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "    OK  $text" -ForegroundColor Green }
function Problem($text) { Write-Host "    !!  $text" -ForegroundColor Yellow }
function Stop-Here($text) { Write-Host ""; Write-Host " Stopped: $text" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host " PLC-Ki TIA helper update" -ForegroundColor White
Write-Host " Folder: $root"

Step 1 "Stopping the running TIA helper"
Get-Process cmd -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*PLC-Ki TIA helper*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process TiaImporter -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Ok "stopped (or it was not running)"

if (-not $NoPull) {
  Step 2 "Getting the newest PLC-Ki (git pull)"
  if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Problem "git is not installed; skipping. Copy the newest PLC-Ki folder here by hand." }
  elseif (-not (Test-Path (Join-Path $root ".git"))) { Problem "$root is not a git clone; skipping." }
  else {
    # A work branch is deleted on GitHub once its pull request is merged. If this folder is still on one,
    # go back to main, but only when nothing was changed here (checkout must not throw edits away).
    git -C $root fetch --prune origin
    $branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne "main" -and $branch -ne "HEAD") {
      git -C $root rev-parse --verify --quiet "refs/remotes/origin/$branch" | Out-Null
      if ($LASTEXITCODE -ne 0) {
        $changed = git -C $root status --porcelain --untracked-files=no
        if ($changed) { Stop-Here "This folder is on the branch '$branch', which is gone from GitHub, and has local changes. Ask for help before switching to main." }
        Problem "The branch '$branch' is gone from GitHub (it was merged). Switching to main."
        git -C $root checkout main
        if ($LASTEXITCODE -ne 0) { Stop-Here "Could not switch to main (see above). Ask for help before going on." }
      } else { Problem "This folder is on the branch '$branch', not main. Updating that branch." }
    }
    git -C $root pull
    if ($LASTEXITCODE -ne 0) { Stop-Here "git pull did not work (see above). If it says your local changes would be overwritten, ask for help before going on." }
    Ok "up to date"
  }
}

Step 3 "Building TiaImporter"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Stop-Here "The .NET 8 SDK is not installed. Get it from https://dotnet.microsoft.com/download/dotnet/8.0 (x64), plus the .NET Framework 4.8 Developer Pack, then run this again."
}
dotnet build $here -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Stop-Here "The build failed (see above). Is TIA Portal V20 installed in C:\Program Files\Siemens\Automation\Portal V20?" }
if (-not (Test-Path $exe)) { Stop-Here "The build did not make $exe." }
Ok $exe

Step 4 "Approving TiaImporter in TIA Openness (Windows asks for administrator rights)"
try {
  $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$(Join-Path $here 'allow-openness.ps1')`"", "-Exe", "`"$exe`"")
  if ($p.ExitCode -eq 0) { Ok "approved" } else { Problem "not approved (exit $($p.ExitCode)). TIA will ask 'Allow access?' the first time: answer Yes to all." }
} catch { Problem "no administrator rights given. TIA will ask 'Allow access?' the first time: answer Yes to all." }

Step 5 "Checking that you may use TIA Openness"
$groups = (whoami /groups) -join "`n"
if ($groups -match "Siemens TIA Openness") { Ok "$env:USERNAME is in 'Siemens TIA Openness'" }
else {
  Problem "$env:USERNAME is not in the group 'Siemens TIA Openness' (or you have not logged off since being added)."
  $answer = Read-Host "    Add $env:USERNAME to the group now? (y/n)"
  if ($answer -match '^[yYjJ]') {
    $cmd = "net localgroup `"Siemens TIA Openness`" `"$env:USERDOMAIN\$env:USERNAME`" /add"
    try { Start-Process cmd -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList "/c", $cmd; Problem "Added. Log off and on again (or restart the VM) before the helper can use TIA." }
    catch { Problem "No administrator rights given. Add the user in Computer Management, Local Users and Groups." }
  }
}

if (-not $NoStart) {
  Step 6 "Starting the TIA helper"
  $settings = Join-Path $env:LOCALAPPDATA "PLC-Ki\tia-helper-settings.cmd"
  if (Test-Path $settings) { Start-Process -FilePath (Join-Path $here "Start-TIA-helper.cmd") -WorkingDirectory $here; Ok "started in the window 'PLC-Ki TIA helper'" }
  else { Problem "Not set up yet: double-click Setup-TIA-helper in this folder." }
}
Write-Host ""
Write-Host " Done." -ForegroundColor Green
