# PLC-Ki server (live status page)

Connects to the PLC **read-only**, reads every tag in the machine model once a second, and shows in plain English:

- which step the sequence is in, for how long, and what it is waiting for (each condition with ✓/✗ and the live value)
- why it is halted (fault, E-stop, Stop pressed, Manual)
- every active alarm: meaning, where in the program, likely cause, fix
- each device: state, whether the program asks for it, and which interlock blocks it
- data exchanged with MES and the filler

and it answers questions about the machine in a chat box (see [Ask about the machine](#ask-about-the-machine)).

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
| `PlcKi:Ask:Enabled` | `true` | Show the "Ask about the machine" box (needs `ANTHROPIC_API_KEY`) |
| `PlcKi:Ask:Model` | `claude-opus-5-5` | Claude model |
| `PlcKi:Ask:Effort` | `medium` | `low`, `medium`, `high` or `max`: how hard Claude thinks. Higher is slower and costs more |
| `PlcKi:HistoryMinutes` | `15` | Minutes of values kept in memory for "what happened?" questions |
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

## Ask about the machine

The box under the summary answers questions such as *"Why is the agitator not running?"*,
*"What happened in the last 10 minutes?"* or *"Explain A07 and how to fix it"*. Follow-up questions keep the context;
**New conversation** starts over.

**Setup:** set the `ANTHROPIC_API_KEY` environment variable (Windows: *Edit the system environment variables* →
*Environment Variables*, or `setx ANTHROPIC_API_KEY sk-ant-...`), then open a **new** command prompt and start
PLC-Ki. The start message says `Ask a question: on (claude-opus-5-5).` Without a key the box tells you what to set;
the rest of the page works as before.

**How it answers:** each question goes to Claude together with the current summary. Claude then looks up what it
needs with read-only lookups (the code is `../PlcKi.Core/Ask/MachineTools.cs`), and the page lists them under the
answer ("Looked at: device M201 · history of Level"):

| Lookup | What it returns |
| --- | --- |
| `get_live_status` | Everything on the status page, as data |
| `read_tags` | Current values by tag id or a search ("temperature") |
| `get_tag_history` | How values changed in the last minutes, with min/max/average |
| `get_recent_events` | Step changes, alarms coming and going, halts, blocked devices, link changes |
| `get_sequence` | Every step: what it does, its limit, the conditions to move on |
| `get_equipment` | A device's interlocks and states, as programmed, plus its live state |
| `get_alarm` | An alarm's location, condition, causes and fix, and whether it is on |
| `search_program` | Where a word or tag is used in steps, interlocks, alarms |

The lookups only read what PLC-Ki has already read. Claude cannot write to the PLC, acknowledge alarms or start
anything. It is told never to suggest bridging or forcing a safety function or interlock.

**Good to know**

- The PC needs internet access to `api.anthropic.com` (HTTPS). The status page itself works offline.
- What is sent to Anthropic: your question, the live summary, and whatever the lookups return (tag values, history,
  step, interlock and alarm descriptions from the machine model). Not the TIA project, and nothing else from the PC.
- Cost: billed to the API key's account, roughly a few cents per question (more with long conversations or
  `Effort` high). Repeated parts of each request are cached, which keeps follow-ups cheap.
- An answer usually takes 5 to 30 seconds.
- Fallback: if Claude's safety filter wrongly declines a question, the API answers it with Anthropic's recommended
  fallback model instead, and the answer says so. This is switched on in `Ask/AskService.cs` (`Fallbacks`).
- History starts when PLC-Ki starts and is kept in memory only (`HistoryMinutes`).

## If the connection fails

| Message | What to check |
| --- | --- |
| Ping fails | Network first: PLCSIM instance running, adapter set up as in `plc-demo/README.md` |
| Ping works, connection times out | `probe` retries with the older challenge login by itself. If both fail, send the full probe output |
| Login / legitimation refused | The CPU's access level needs a password: set `PLCKI_PLC_PASSWORD` |
| `the PLC has no program loaded` | The PLC (or PLCSIM instance) is running without a program: download it from TIA Portal. PLC-Ki checks again every 30 s |
| `the program on the PLC is not the one in the machine model` | The data blocks the model reads are not on the PLC: download the right program, or start PLC-Ki with the model for this machine |
| `the PLC answered no tag lookup` | The login worked but the PLC refused to look up symbols. Run `probe`: it tries the older login by itself. If that works, set `Security` to `LegacyChallenge`; if not, check the CPU's protection settings in TIA |
| `symbol ... not found` for some tags | The PLC program differs from the model: compare with the TIA project, re-download the demo |

## How it works

| File | What |
| --- | --- |
| `Live/S7CommPlusSource.cs` | Connects with the driver, looks up each symbol once, then reads all tags in one batch per poll |
| `Live/PollingService.cs` | Background loop: read, evaluate, reconnect with 2 s → 30 s back-off |
| `Live/SnapshotFileSource.cs` | Test source: values from a JSON file |
| `Probe.cs` | The `probe` command |
| `Ask/AskService.cs`, `Ask/SystemPrompt.cs` | The question loop with Claude (official `Anthropic` NuGet package) and its instructions |
| `../PlcKi.Core/Ask/MachineTools.cs` | The read-only lookups Claude can use |
| `../PlcKi.Core/Status/MachineHistory.cs` | Recent events and values, in memory |
| `../PlcKi.Core/Status/StatusEngine.cs` | Turns values + model into the plain-English status |
| `../PlcKi.Core/Expressions/Expr.cs` | The small condition language used in the model (`Level >= LevelA AND NOT AnyFault`) |
| `wwwroot/` | The page (no external libraries; `ask.js` is the chat box) |

API: `/api/status` (everything on the page as JSON), `/api/values` (raw values with symbols), `/api/model`,
`POST /api/ask` (`{"question": "...", "conversationId": null}`).

## Driver

[DotNetProjects.S7CommPlusDriver](https://www.nuget.org/packages/DotNetProjects.S7CommPlusDriver) 6.1.0 from NuGet
(LGPL-3.0-or-later), a maintained fork of thomas-v2/S7CommPlusDriver. It is reverse-engineered, not a Siemens product.
It does TLS in managed code (no OpenSSL DLLs) and supports the older challenge login with the PLCSIM key family.

**Read-only:** the driver can write and start/stop the CPU only when `WriteEnabled` is set; it is off by default.
PLC-Ki never sets it and calls none of those functions. `tests/PlcKi.Tests/ReadOnlyTests.cs` reads the compiled
app and fails the build if that changes.
