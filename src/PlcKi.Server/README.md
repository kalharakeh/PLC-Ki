# PLC-Ki server (live status page)

Connects to the PLC **read-only**, reads every tag in the machine model once a second, and shows in plain English:

- which step the sequence is in, for how long, and what it is waiting for (each condition with ✓/✗ and the live value)
- why it is halted (fault, E-stop, Stop pressed, Manual)
- every active alarm: meaning, where in the program, likely cause, fix
- each device: state, whether the program asks for it, and which interlock blocks it
- data exchanged with MES and the filler

The meaning comes from the machine model, `models/demo-mixing-station.json`. For the demo it is written by hand
from the SCL sources; later the archive importer will generate it from the `.zap20`.

## Run it on your PC

You need the **.NET 8 SDK, x64** (<https://dotnet.microsoft.com/download/dotnet/8.0>). Check with `dotnet --list-sdks`.
The PC must reach the PLC: `ping 192.168.10.10` should answer.

Open a command prompt in the repository folder (the one with `PlcKi.sln`).

**1. Connection test.** Reads every tag once, prints them next to their TIA symbols, then prints the status text:

```
dotnet run --project src\PlcKi.Server -- probe
```

The first run downloads packages and builds, which takes a minute. At the end it says either
`OK: all 83 tags read`, or which tags failed, or `NOT CONNECTED` with the reason.

**2. Start the app:**

```
dotnet run --project src\PlcKi.Server
```

Open <http://localhost:5080>. Stop it with Ctrl+C.

### Settings

`src/PlcKi.Server/appsettings.json`, or override on the command line after `--`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `PlcKi:Plc:Address` | `192.168.10.10` | PLC IP address |
| `PlcKi:Plc:PollMs` | `1000` | Read interval |
| `PlcKi:Plc:Password` | empty | Only if the CPU has an access password. Better: environment variable `PLCKI_PLC_PASSWORD` |
| `PlcKi:Plc:Security` | `Auto` | `Auto` = TLS first, then the older challenge login. `Tls` or `LegacyChallenge` to force one |
| `PlcKi:Model` | `models/demo-mixing-station.json` | Machine model |
| `PlcKi:Source` | `s7` | `s7` = live PLC, `snapshot` = values from a JSON file |
| `PlcKi:SnapshotFile` | `models/snapshots/step10-xv101-not-open.json` | Used when Source is `snapshot` |
| `Urls` | `http://localhost:5080` | Where the page is served. Only this PC can open it by default |

Example: `dotnet run --project src\PlcKi.Server -- probe --PlcKi:Plc:Address=192.168.10.20`

### Without a PLC

```
dotnet run --project src\PlcKi.Server -- --PlcKi:Source=snapshot --PlcKi:SnapshotFile=models/snapshots/step30-agitator-blocked.json
```

Snapshot files are plain JSON (`"Level": 15.0`, `"StepTime": "T#20S"`). The app reads the file again every second,
so you can edit values and watch the page change.

## If the connection fails

| Message | What to check |
| --- | --- |
| Ping fails | Network first: PLCSIM instance running, adapter set up as in `plc-demo/README.md` |
| Ping works, connection times out | `probe` retries with the older challenge login by itself. If both fail, send the full probe output |
| Login / legitimation refused | The CPU's access level needs a password: set `PLCKI_PLC_PASSWORD` |
| `symbol ... not found` for some tags | The PLC program differs from the model: compare with the TIA project, re-download the demo |

## How it works

| File | What |
| --- | --- |
| `Live/S7CommPlusSource.cs` | Connects with the driver, looks up each symbol once, then reads all tags in one batch per poll |
| `Live/PollingService.cs` | Background loop: read, evaluate, reconnect with 2 s → 30 s back-off |
| `Live/SnapshotFileSource.cs` | Test source: values from a JSON file |
| `Probe.cs` | The `probe` command |
| `../PlcKi.Core/Status/StatusEngine.cs` | Turns values + model into the plain-English status |
| `../PlcKi.Core/Expressions/Expr.cs` | The small condition language used in the model (`Level >= LevelA AND NOT AnyFault`) |
| `wwwroot/` | The page (no external libraries, works offline) |

API: `/api/status` (everything on the page as JSON), `/api/values` (raw values with symbols), `/api/model`.

## Driver

[DotNetProjects.S7CommPlusDriver](https://www.nuget.org/packages/DotNetProjects.S7CommPlusDriver) 6.1.0 from NuGet
(LGPL-3.0-or-later), a maintained fork of thomas-v2/S7CommPlusDriver. It is reverse-engineered, not a Siemens product.
It does TLS in managed code (no OpenSSL DLLs) and supports the older challenge login with the PLCSIM key family.

**Read-only:** the driver can write and start/stop the CPU only when `WriteEnabled` is set; it is off by default.
PLC-Ki never sets it and calls none of those functions. `tests/PlcKi.Tests/ReadOnlyTests.cs` reads the compiled
app and fails the build if that changes.
