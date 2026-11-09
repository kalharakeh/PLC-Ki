# PLC-Ki server (live status page)

Connects to the PLC **read-only**, reads every tag in the machine model once a second, and shows in plain English:

- which step the sequence is in, for how long, and what it is waiting for (each condition with ✓/✗ and the live value)
- why it is halted (fault, E-stop, Stop pressed, Manual)
- every active alarm: meaning, where in the program, likely cause, fix
- each device: state, whether the program asks for it, and which interlock blocks it
- data exchanged with MES and the filler

and it answers questions about the machine in a chat box (see [Ask about the machine](#ask-about-the-machine)).

The meaning comes from the machine model, `models/demo-mixing-station.json`. For the demo it is written by hand
from the SCL sources; the `import` command can also have Claude write it from a TIA export (see
[Machine model from a TIA export](#machine-model-from-a-tia-export)).

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

**2. Start the app:** double-click **`Start-PLC-Ki`** in the repository folder. It opens <http://localhost:5080> in
the browser when PLC-Ki is ready; close its window to stop PLC-Ki. (Same as `dotnet run --project src\PlcKi.Server`.)

**3. Get started.** The first time an administrator logs in, PLC-Ki opens **Setup, Get started**: one question per
page (which setup you have, the PLC address with a Test button, TIA Portal, the Claude key, your program). Until
everything is set up, the start page shows a *Getting started* card with what is done and what comes next. Click the
connection light at the top right at any time for what it means and what to do.

## The pages

Everyone with a login sees **Active alarm** (the start page: what is wrong, how to fix it, where it is in the machine
and in the program), **Plant** (sequence, devices, data exchange), **Alarms**, **Logic** (each network drawn as a rung
with live values; the first red contact is what blocks) and **Code** (the source with live values). **Ask** opens the
chat on every page.

Administrators also see **Setup**, where everything that used to be a command is a button:

| Page | What |
| --- | --- |
| Connections | Pick the setup (same PC as TIA, PC with a TIA VM, server + laptop), the PLC address, and where TIA runs. A diagram shows each link green or red. |
| Program | Drop a TIA archive (.zap20) or an export (.zip). PLC-Ki has TIA export it, Claude builds the machine model, you check it and press *Use this program*. |
| Block generator | Say what you need. Claude writes the blocks, TIA imports and compiles them, PLCSIM runs them. A real PLC needs the version typed here and the address typed at the TIA PC. |
| Settings | Machine model, Claude API key, network access, logins. |

Where TIA runs (Setup, Connections):

- **This PC**: PLC-Ki starts `TiaImporter serve --local` itself, in its own window (only this PC can reach it). Set
  the TiaImporter path once.
- **Another PC or VM**: once, double-click `tools\TiaImporter\Setup-TIA-helper` there; after that the helper starts by itself at log-in (see [tools/TiaImporter/README.md](../../tools/TiaImporter/README.md)). Enter its address and token in PLC-Ki.
- **My laptop (PLC-Ki on a server)**: PLC-Ki Link on the laptop carries the TIA work (see below).

### PLC-Ki on a server: PLC-Ki Link

When PLC-Ki runs on a server that reaches neither the PLC nor TIA, a laptop that reaches both runs **PLC-Ki Link**.
Link only connects out to the server (no firewall or VPN changes on the plant side). It reads the PLC read-only with
the same driver and sends the values once a second, and with `--tia` it starts `TiaImporter serve --local` on the
laptop and carries the server's TIA calls to it. A real-PLC download is still confirmed in that helper's window, on
the laptop.

1. On the server, Setup, Connections: choose **Server + my laptop**, save, and click **Make a key**. Turn on
   "Other computers may open PLC-Ki" (Settings) so the laptop can reach the server.
2. On the laptop (needs the .NET 8 SDK and a clone of this repository), in the PLC-Ki folder:

```
dotnet run --project src\PlcKi.Server -- link --server http://<server>:5080 --key <key> --tia "C:\PLCKi\PLC-Ki\tools\TiaImporter\bin\Release\net48\TiaImporter.exe"
```

Options: `--plc <address>` reads another address than the server's setting, `--plc-password` (or
`PLCKI_PLC_PASSWORD`) for a protected CPU (the password stays on the laptop), `--plcsim-ip` and `--allow-real` for
the TIA helper, `--snapshot <file>` sends test values instead of reading a PLC. Without `--tia`, Link only carries
the PLC values. The Connections page shows Link, the PLC and TIA turning green.

Long work (TIA export, building a model, the block generator) runs as a job: the same command as on the command line,
started by PLC-Ki, with its progress and log on the page. One job at a time.

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

Ready-made files in `models/snapshots/`: `step10-xv101-not-open` (one alarm, A02), `step30-agitator-blocked`,
`step30-mix-heat`, `step50-drain`, and `step30-estop-and-drive` (two alarms: A01 emergency stop, then A08 drive not ready).

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

## Machine model from a TIA export

The machine model (tags, sequences, devices, alarms, texts) can be written by Claude from the program instead of by
hand. Input is the export from `tools/TiaImporter export` (the zip or its folder); the TIA PC does not need an API
key, only the PC that runs this command.

```
dotnet run --project src\PlcKi.Server -- import --export C:\Users\me\Downloads\export.zip --out models\generated.json
dotnet run --project src\PlcKi.Server -- --PlcKi:Model=models\generated.json     # start PLC-Ki with it
```

| Option | What |
| --- | --- |
| `--plc <name>` | Which PLC, when the export has more than one |
| `--compare <model.json>` | Compare the result with another model, e.g. `models\demo-mixing-station.json` |
| `--effort low/medium/high/max` | How hard Claude thinks (default `high`; the import runs once, so quality first) |
| `--model <id>` | Claude model (default `claude-opus-5-5`) |
| `--digest-only <file>` | Only write the program text Claude would read; no API call, no key needed |

How it works:

1. `ProgramExport` reads the export: blocks, interfaces with comments, calls, SCL sources, tag tables, and every
   member PLC-Ki can read (exact symbols and types).
2. `ProgramDigest` writes that as compact text (the demo is about 12k tokens).
3. Claude writes the model as JSON (`Import/ModelPrompt.cs` has the format and the rules).
4. `ModelCheck` checks it against the program: every symbol must exist (with a "did you mean" hint), types come
   from the program, and all conditions must parse. Problems go back to Claude with the draft, up to 3 rounds.
5. The model is written only to the file you name. Review it before using it at a machine.

It takes a few minutes and costs roughly $0.50 to $2 for a program the size of the demo. SCL is read in full;
for LAD and GRAPH blocks Claude only sees the interface and the calls for now, so their logic is not decoded yet.

## Write PLC blocks from a request

```
dotnet run --project src\PlcKi.Server -- generate --out C:\temp\gen\valve --request "Make me a valve FB in LAD to our standard"
```

Claude writes FBs, FCs, DBs, data types and tag tables (SCL, LAD, FBD, GRAPH) into a versioned package folder;
`tools/TiaImporter import-blocks` imports and compiles it in TIA, and `generate --fix` sends TIA's errors back.
Full workflow: [docs/block-generator.md](../../docs/block-generator.md). The same loop runs from Setup, Block
generator, without commands.

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
| `Import/ImportCommand.cs`, `Import/ModelBuilder.cs`, `Import/ModelPrompt.cs` | The `import` command: Claude writes the machine model from a program export |
| `../PlcKi.Core/Import/` | Reads the export, writes the program digest, checks a model against the program |
| `Ask/AskService.cs`, `Ask/SystemPrompt.cs` | The question loop with Claude (official `Anthropic` NuGet package) and its instructions |
| `../PlcKi.Core/Ask/MachineTools.cs` | The read-only lookups Claude can use |
| `../PlcKi.Core/Status/MachineHistory.cs` | Recent events and values, in memory |
| `../PlcKi.Core/Status/StatusEngine.cs` | Turns values + model into the plain-English status |
| `../PlcKi.Core/Expressions/Expr.cs` | The small condition language used in the model (`Level >= LevelA AND NOT AnyFault`) |
| `Storage/StorageService.cs`, `Storage/StorageEndpoints.cs` | Setup › Settings › Storage: what is kept, the limits (pruned at start and daily), export, clear |
| `../PlcKi.Core/Storage/JsonLinesLog.cs` | The change log (`value-changes.jsonl`), put aside into dated files as it grows |
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

## Login and settings

The first time PLC-Ki starts it asks (on this PC only) for an administrator login. After that every page needs a
login, and administrators get the **Setup** pages for the PLC and TIA connections, program import, block generator,
machine model, Claude API key, network access and users. Values saved there go to `%LOCALAPPDATA%\PLC-Ki\settings.json` when started by hand, and are used after
a restart. Command-line options and environment variables still win. For installing on a plant PC as a Windows
service, see [installer/README.md](../../installer/README.md).
