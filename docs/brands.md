# More PLC brands

PLC-Ki's machine model, status engine, alarms and Ask are the same for every brand. Only two things depend on the brand:

1. **Reading the program** (`IProgramReader`, `src/PlcKi.Core/Import/ProgramReaders.cs`): turns the brand's files into the
   brand-neutral `ProgramExport` (blocks, interfaces, source, call structure, readable tags with exact symbols).
2. **Reading live values** (`IPlcSource`, `src/PlcKi.Server/Live/IPlcSource.cs`): reads those symbols from the PLC, read-only.

| Brand | Program input | Live data | Status |
|---|---|---|---|
| Siemens S7-1500/1200 | TIA archive (.zap) -> TiaImporter export | S7CommPlus | done |
| Beckhoff TwinCAT 3 | project folder zipped, or .tnzip archive | ADS (Beckhoff.TwinCAT.Ads) | program reader done; ADS next |
| Allen-Bradley Logix | .L5X export (Studio 5000 "Save As" .L5X) | CIP via libplctag | planned |
| CODESYS | PLCopen XML export | OPC UA | planned |

## Beckhoff TwinCAT 3

- Upload on Setup > Program: zip the TwinCAT solution folder (the one with the .tsproj and the PLC project folder), or use
  File > Save as Archive (.tnzip) in TwinCAT XAE. No TwinCAT is needed to read it.
- Read: every .TcPOU (PROGRAM, FUNCTION_BLOCK, FUNCTION, with methods, actions and properties), .TcGVL, .TcDUT
  (structs, enums, aliases), .TcTTO (tasks, which programs they call). Only files listed in the .plcproj are used.
- Structured Text is passed to Claude as source. SFC, LD, FBD and CFC bodies are listed by language but not decoded yet.
- Readable tags are ADS symbols: `MAIN.fbInlet.bOpenCmd`, `GVL_IO.bStartButton`, `GVL_Alarms.aAlarm[2]`, through FB
  instances (with EXTENDS), structs, standard TON/TOF/TP/R_TRIG/CTU members and arrays. Enums read as their number.
- Not read yet: TwinCAT EventLogger event classes (alarm texts), the I/O tree (which terminal a %I* variable is linked to).

### Test setup for live data (next step)

- TwinCAT 3 XAE + XAR on the VM, with the PLC project activated and running (Run mode, or a 7-day trial licence).
- An ADS route between the PC that runs PLC-Ki and the VM (TwinCAT router on the VM; PLC-Ki uses the
  Beckhoff ADS TCP router so the PC needs no TwinCAT install). AMS Net ID of the VM target, port 851 for the first PLC.
- Windows firewall on the VM: allow TCP 48898 (ADS) from the PC.
