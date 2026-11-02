# PLC-Ki (PLC Troubleshooter)

Explains in plain English what a Siemens S7-1500 / S7-1200 machine is doing right now, and why an alarm is on.

You give it the TIA Portal archive (`.zap20`) and the PLC's IP address. It decodes the program (LAD, FBD, SCL,
GRAPH), reads live values from the PLC, and tells you:

- which step each sequence is in and what it is waiting for, with live values
- what is running, at what speed, and why something is *not* running (which interlock is missing)
- what data is exchanged with other systems
- for every active alarm: what it means, where it is raised in the program, why it is on, and how to fix it

You can also just ask, for example *"Why is the agitator stopped?"*. Answers cite the block and network they
are based on, so you can check them in TIA Portal.

> **Read-only.** The app never writes to a PLC. There is no write code path, not even a disabled one.

## How it works

| Part | How |
| --- | --- |
| Program | TIA Portal Openness opens the `.zap20` in the background and exports every block, tag table and alarm. No manual exports. |
| Live data | S7CommPlus, the protocol TIA Portal and HMIs use: no OPC UA license, no PUT/GET, no change to the PLC. PLC Web API as fallback. |
| Explanations | Rule-based live status, plus Claude for questions, using tools that look up the real program and live values. |
| App | C# / .NET server with a browser UI. Runs on a desktop first, later as a service on a plant PC. |

Full plan and architecture: [PLC Troubleshooter plan](https://claude.ai/artifact/9CgfxZD1ehAAZ6yVGEvczf)

## Repository

| Folder | What |
| --- | --- |
| `plc-demo/` | Demo PLC program (mixing station) for PLCSIM Advanced, with plant simulation, fault injection and an alarm answer key |
| `tools/TiaImporter/` | TIA Openness tool: export a `.zap20` archive, load the demo into a TIA project |
| `src/PlcKi.Core/` | Machine model, condition language and the plain-English status engine (no PLC code) |
| `src/PlcKi.Server/` | The web app: live read-only PLC link, status page, `probe` connection test. See its README |
| `models/` | Machine models (`demo-mixing-station.json`) and test snapshots taken from watch tables |
| `tests/` | Unit tests (`dotnet test`), including a check that the app calls no PLC write or start/stop function |

## Requirements

- Windows PC with **TIA Portal V20** (Openness included) for importing archives
- Network access to the PLC (if TIA Portal can go online from the PC, the app can too)
- S7-1500 firmware V2.9+ or S7-1200 firmware V4.3+ for the live link
- `ANTHROPIC_API_KEY` environment variable for the question-and-answer feature

## Status

| Phase | Status |
| --- | --- |
| 0. Setup: repo, PLCSIM Advanced VM on the network | Done |
| 1. Demo program and `.zap20` importer | Demo runs full batches in PLCSIM. Importer written, not yet built against TIA |
| 2. Live status page | First version: works on test data, first live test on PLCSIM pending. Machine model is hand-written for now |
| 3. Ask questions (Claude) | Planned |
| 4. Real plant archives, Web API fallback, S7-1200 hardware | Planned |
| 5. Plant PC deployment | Planned |

## Quick start (demo)

Live status page, against the demo PLC in PLCSIM (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```
dotnet run --project src\PlcKi.Server -- probe     # connection test: reads every tag once
dotnet run --project src\PlcKi.Server              # then open http://localhost:5080
```

Details: [src/PlcKi.Server/README.md](src/PlcKi.Server/README.md).

Archive importer:

1. Build `tools/TiaImporter` (see its README).
2. Follow `plc-demo/README.md` to load the demo into TIA V20 and run it in PLCSIM Advanced.
3. Archive the project and run `TiaImporter export --archive MixingDemo.zap20 --out C:\temp\demo-export`.
