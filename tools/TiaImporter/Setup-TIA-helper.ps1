# PLC-Ki TIA helper setup (one time, on the TIA PC or VM). Started by Setup-TIA-helper.cmd.
#
# Shows a small window for the token and the PLCSIM address, then:
#   1. opens the port for "TiaImporter serve" (URL reservation + firewall rule; Windows asks for administrator rights once)
#   2. saves the settings in %LOCALAPPDATA%\PLC-Ki\tia-helper-settings.cmd (only for this Windows user)
#   3. puts "PLC-Ki TIA helper" in this user's Startup folder, so it starts at every log-in
#   4. starts the helper now, in its own window
# Run it again to change the token or the addresses, or to turn the start at log-in off.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$here = $PSScriptRoot
$exe = Join-Path $here "bin\Release\net48\TiaImporter.exe"
$starter = Join-Path $here "Start-TIA-helper.cmd"
$settingsDir = Join-Path $env:LOCALAPPDATA "PLC-Ki"
$settingsFile = Join-Path $settingsDir "tia-helper-settings.cmd"
$shortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "PLC-Ki TIA helper.lnk"
$title = "PLC-Ki TIA helper setup"

function Say($text, $icon = "Information") {
  [void][System.Windows.Forms.MessageBox]::Show($text, $title, "OK", $icon)
}

if (-not (Test-Path $exe)) {
  $build = [System.Windows.Forms.MessageBox]::Show("TiaImporter is not built on this computer yet.`n`nBuild it now? This takes about a minute (needs the .NET 8 SDK and TIA Portal V20).", $title, "YesNo", "Question")
  if ($build -ne "Yes") { exit 1 }
  & (Join-Path $here "Update-TIA-helper.ps1") -NoPull -NoStart
  if (-not (Test-Path $exe)) { Say "The build did not work. See the black window for why." "Warning"; exit 1 }
}

# What was set up before, if anything
$old = @{ PLCKI_TIA_TOKEN = ""; PLCKI_TIA_PLCSIM = "192.168.10.10"; PLCKI_TIA_PORT = "5090"; PLCKI_TIA_ALLOW_REAL = "" }
if (Test-Path $settingsFile) {
  foreach ($line in Get-Content $settingsFile) {
    if ($line -match '^set "(PLCKI_TIA_\w+)=(.*)"$') { $old[$Matches[1]] = $Matches[2] }
  }
}

# ---------- the window ----------
$font = New-Object System.Drawing.Font("Segoe UI", 10)
$form = New-Object System.Windows.Forms.Form
$form.Text = $title
$form.Font = $font
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.BackColor = [System.Drawing.Color]::White
$form.ClientSize = New-Object System.Drawing.Size(520, 470)

$y = 20
function Add-Label($text, $size = 10, $bold = $false, $height = 24, $color = $null) {
  $l = New-Object System.Windows.Forms.Label
  $l.Text = $text
  $l.Font = New-Object System.Drawing.Font("Segoe UI", $size, $(if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }))
  if ($color) { $l.ForeColor = $color }
  $l.Location = New-Object System.Drawing.Point(24, $script:y)
  $l.Size = New-Object System.Drawing.Size(472, $height)
  $form.Controls.Add($l)
  $script:y += $height
  return $l
}
function Add-Box($value, $password = $false) {
  $t = New-Object System.Windows.Forms.TextBox
  $t.Text = $value
  $t.Location = New-Object System.Drawing.Point(24, $script:y)
  $t.Size = New-Object System.Drawing.Size(472, 28)
  if ($password) { $t.UseSystemPasswordChar = $false }
  $form.Controls.Add($t)
  $script:y += 40
  return $t
}
$grey = [System.Drawing.Color]::FromArgb(110, 110, 115)

[void](Add-Label "Set up the TIA helper" 16 $true 36)
[void](Add-Label "PLC-Ki on your PC sends TIA work here: importing a program and the block generator. After this setup the helper starts by itself when you log in." 9.5 $false 44 $grey)
$y += 8
[void](Add-Label "Token (a password you make up, 8 or more letters or digits)" 10 $true)
$token = Add-Box $old.PLCKI_TIA_TOKEN
[void](Add-Label "PLCSIM address for test downloads (empty = no PLCSIM downloads)" 10 $true)
$plcsim = Add-Box $old.PLCKI_TIA_PLCSIM
[void](Add-Label "Real PLCs that may be loaded (empty = never)" 10 $true)
$real = Add-Box $old.PLCKI_TIA_ALLOW_REAL
[void](Add-Label "Each real-PLC download is still confirmed by typing its address in the helper window." 9 $false 22 $grey)
$y += 6
$auto = New-Object System.Windows.Forms.CheckBox
$auto.Text = "Start the helper by itself when I log in"
$auto.Checked = $true
$auto.Location = New-Object System.Drawing.Point(24, $y)
$auto.Size = New-Object System.Drawing.Size(472, 26)
$form.Controls.Add($auto)
$y += 44

$ok = New-Object System.Windows.Forms.Button
$ok.Text = "Set up and start"
$ok.Location = New-Object System.Drawing.Point(316, $y)
$ok.Size = New-Object System.Drawing.Size(180, 38)
$ok.BackColor = [System.Drawing.Color]::FromArgb(37, 99, 201)
$ok.ForeColor = [System.Drawing.Color]::White
$ok.FlatStyle = "Flat"
$ok.FlatAppearance.BorderSize = 0
$form.Controls.Add($ok)
$cancel = New-Object System.Windows.Forms.Button
$cancel.Text = "Cancel"
$cancel.Location = New-Object System.Drawing.Point(200, $y)
$cancel.Size = New-Object System.Drawing.Size(104, 38)
$cancel.FlatStyle = "Flat"
$cancel.DialogResult = "Cancel"
$form.Controls.Add($cancel)
$form.AcceptButton = $ok
$form.CancelButton = $cancel

$ip = '^\d{1,3}(\.\d{1,3}){3}$'
$ok.Add_Click({
  $t = $token.Text.Trim()
  if ($t -notmatch '^[A-Za-z0-9_.-]{8,}$') { Say "The token needs 8 or more letters, digits, - _ or . (no spaces or other signs)." "Warning"; return }
  if ($plcsim.Text.Trim() -and $plcsim.Text.Trim() -notmatch $ip) { Say "The PLCSIM address should look like 192.168.10.10." "Warning"; return }
  foreach ($a in ($real.Text -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    if ($a -notmatch $ip) { Say "Real PLC addresses should look like 192.168.0.1, separated by commas." "Warning"; return }
  }
  $form.DialogResult = "OK"
  $form.Close()
})

if ($form.ShowDialog() -ne "OK") { exit 0 }

$port = $old.PLCKI_TIA_PORT
$user = "$env:USERDOMAIN\$env:USERNAME"

# ---------- 1. port: needs administrator rights once ----------
$admin = @"
netsh http delete urlacl url=http://+:$port/ | Out-Null
netsh http add urlacl url=http://+:$port/ user="$user" | Out-Null
netsh advfirewall firewall delete rule name="PLC-Ki TiaImporter" | Out-Null
netsh advfirewall firewall add rule name="PLC-Ki TiaImporter" dir=in action=allow protocol=TCP localport=$port | Out-Null
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($admin))
try {
  Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList "-NoProfile", "-EncodedCommand", $encoded
} catch {
  Say "Windows did not give administrator rights, so port $port could not be opened.`nRun this setup again and answer Yes when Windows asks." "Warning"
  exit 1
}

# ---------- 2. settings for this user ----------
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
@(
  "@rem PLC-Ki TIA helper settings, written by Setup-TIA-helper. Run that again to change them.",
  "set `"PLCKI_TIA_TOKEN=$($token.Text.Trim())`"",
  "set `"PLCKI_TIA_PLCSIM=$($plcsim.Text.Trim())`"",
  "set `"PLCKI_TIA_PORT=$port`"",
  "set `"PLCKI_TIA_ALLOW_REAL=$(($real.Text -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ',')`""
) | Set-Content -Path $settingsFile -Encoding ASCII

# ---------- 3. start at log-in ----------
if ($auto.Checked) {
  $ws = New-Object -ComObject WScript.Shell
  $lnk = $ws.CreateShortcut($shortcut)
  $lnk.TargetPath = $starter
  $lnk.WorkingDirectory = $here
  $lnk.Description = "PLC-Ki TIA helper (TiaImporter serve)"
  $lnk.Save()
} elseif (Test-Path $shortcut) {
  Remove-Item $shortcut
}

# ---------- 4. start it now (stop an older one first, so the new settings apply) ----------
Get-Process cmd -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*PLC-Ki TIA helper*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process TiaImporter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Start-Process -FilePath $starter -WorkingDirectory $here

$ips = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
  Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } | ForEach-Object { "   http://$($_.IPAddress):$port" }) -join "`n"
Say ("Done. The helper runs in the window `"PLC-Ki TIA helper`".`n`n" +
  "In PLC-Ki on your PC (Setup, Get started, TIA Portal) enter:`n" +
  "VM address:`n$ips`nToken: $($token.Text.Trim())`n`n" +
  $(if ($auto.Checked) { "It starts by itself the next time you log in." } else { "It does not start by itself. Double-click Start-TIA-helper to start it." }))
