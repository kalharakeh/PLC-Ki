# PLC-Ki on a plant PC

PLC-Ki can run as a **Windows service** on a PC in the plant network: it starts with Windows, keeps running when
nobody is logged on, and restarts by itself after a crash. People open it in a browser.

## Get the installer

Every pull request and every push to `main` builds `PLC-Ki-Setup-<version>.exe` on GitHub, installs it on a test
Windows machine, checks the service answers, and uninstalls it again. To download it: GitHub → **Actions** →
the latest *build-and-test* run → **Artifacts** → `PLC-Ki-Setup` (a zip with the .exe inside).

Or build it yourself (needs the .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isdl.php), `winget install JRSoftware.InnoSetup`):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 0.1.0
# -> out\PLC-Ki-Setup-0.1.0.exe
```

The installer contains .NET itself, so the plant PC needs nothing else. Windows 10/11 or Server 2016+, 64-bit.

## Install

1. Run `PLC-Ki-Setup-….exe` as administrator. Tick *Allow other computers … through the Windows firewall* only if
   people should open PLC-Ki from other PCs.
2. At the end it opens <http://localhost:5080>. **First start:** create the administrator login. This works only on
   the PLC-Ki PC itself, not from another computer.
3. You land on **Settings**:
   - **PLC connection**: the PLC's IP address, then **Test** (checks the PLC answers on the S7 port 102; it sends nothing).
     PLC access password only if the CPU has one.
   - **Machine model**: upload the `.json` the archive importer made for this machine, and pick it.
   - **Ask about the machine**: paste the Claude API key (`sk-ant-…`). A service does not see your own
     `ANTHROPIC_API_KEY` variable, so on a plant PC enter the key here.
   - **Network and access**: port, whether other computers may open PLC-Ki, and whether a screen next to the
     machine may view it without logging in.
4. **Save**, then **Restart PLC-Ki**. The page is back after a few seconds.
5. Under **Users**, add a login per person or per shift. *Operators* see every view and can ask questions;
   *administrators* can also change settings and users.

To update, run the newer installer over the old one. Settings and users are kept.
To remove, use *Apps & features* → PLC-Ki. The data folder stays (delete it by hand if you want).

## Where things are

| What | Where |
| --- | --- |
| Program | `C:\Program Files\PLC-Ki` |
| Settings, users, uploaded models, encryption keys | `C:\ProgramData\PLC-Ki` (only Administrators and the service can open it) |
| Service | `PLC-Ki` in *Services* (`services.msc`), account *Local Service*, start *Automatic (delayed)* |
| Log messages | *Event Viewer* → Windows Logs → Application, source `PLC-Ki` |

Passwords of PLC-Ki users are stored only as salted hashes (PBKDF2-SHA256). The PLC password and the Claude API key
are stored encrypted (ASP.NET Data Protection, keys protected with Windows DPAPI for this machine).
After 5 wrong passwords a user name is locked for a minute. A login lasts 12 hours (one shift), renewed while in use.

## Running by hand (development)

`dotnet run --project src\PlcKi.Server` works as before, with the same login and settings page. Started by hand,
PLC-Ki keeps its data in `%LOCALAPPDATA%\PLC-Ki` instead, so it never mixes with an installed service.
Another folder: `--Plant:DataDir=D:\plcki-data` or the `PLCKI_DATADIR` environment variable.
Values on the command line and in environment variables still win over the settings page.

## Not done yet

- **HTTPS.** The page is plain HTTP. On a plant network that is usually acceptable for a read-only viewer, but
  passwords travel unencrypted. Keep *other computers* off, or put PLC-Ki behind the site's HTTPS proxy, until this is added.
- **Several PLCs.** One PLC-Ki service watches one PLC with one machine model. For a second machine, a second
  instance (another port and data folder) is the way for now.
- **Windows logins (Active Directory).** Users are PLC-Ki's own.
