# TiaImporter

Command-line helper that uses **TIA Portal V20 Openness**. Only `download` and `serve` (with `--plcsim-ip`) talk to a
PLC, and only to PLCSIM Advanced.

```
TiaImporter export --archive <project.zap20> --out <empty folder>
TiaImporter setup-demo --project <project.ap20> --demo <plc-demo folder> [--plc <PLC name>]
TiaImporter import-blocks --project <project.ap20> --package <package folder> [--plc <PLC name>] [--version v002] [--replace] [--keep-on-error]
TiaImporter download --project <project.ap20> --plcsim-ip <address> [--plc <PLC name>]
TiaImporter serve --token <secret> [--port 5090] [--plcsim-ip <address>] [--packages C:\PLCKi\gen] [--projects <folder>]
```

`download` and `serve` download only to PLCSIM: the PLC's address in the project must be the PLCSIM address and the
PC interface must be a PLCSIM adapter; software only. `serve` is what `PlcKi.Server develop` talks to.

`import-blocks` loads blocks written by `PlcKi.Server generate` into the project you name, compiles, and writes
`import-report.json` for the next fix round. See [docs/block-generator.md](../../docs/block-generator.md).

`export` opens the archive without showing TIA (into a temporary folder; the archive is not changed) and writes,
per PLC:

```
<out>\manifest.json            project, PLCs, CPU types, IP addresses, counts, problems (e.g. protected blocks)
<out>\<PLC>\Blocks\...xml      every block, any language (LAD, FBD, SCL, STL, GRAPH), SimaticML, folders kept
<out>\<PLC>\Types\...xml       PLC data types
<out>\<PLC>\Tags\...xml        tag tables
<out>\<PLC>\SclSource\*.scl    readable source of SCL blocks
```

## Requirements

- Windows with **TIA Portal V20** including the Openness option (selected by default in the installer)
- A valid STEP 7 Professional license
- Your Windows user in the local group **Siemens TIA Openness** (Computer Management > Local Users and Groups),
  then sign out and in again
- The first time the tool starts TIA, TIA asks you to allow access for `TiaImporter.exe`, and again after every
  rebuild. To skip that prompt, approve your own build once (asks for administrator rights):

  ```
  powershell -ExecutionPolicy Bypass -File tools\TiaImporter\allow-openness.ps1
  ```

  It writes the same Openness whitelist entry TIA writes when you answer "Yes to all" (path, date and SHA-256 of
  the exe under `HKLM\SOFTWARE\Siemens\Automation\Openness\20.0\Whitelist`). Run it again after each rebuild.
  Only the TIA PC needs this.

## Build

Visual Studio 2022 (".NET desktop development" workload), or the .NET 8 SDK plus the .NET Framework 4.8
Developer Pack:

```
cd tools\TiaImporter
dotnet build -c Release
```

If TIA is not installed in `C:\Program Files\Siemens\Automation\Portal V20`, build with
`-p:TiaInstallDir="D:\Your\Path\Portal V20"` and set the environment variable `TIA_INSTALL_DIR` to the same path.

Not yet compiled against a real TIA install: the first build on your PC may need small fixes.
