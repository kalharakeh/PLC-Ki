# Block generator

Describe what you need in plain words; Claude writes the PLC program parts and TIA Portal imports and compiles them.

- **What it writes:** FBs, FCs, global and instance DBs, PLC data types and tag tables, in SCL, LAD, FBD or GRAPH
  (S7-1500 and S7-1200, TIA Portal V20). F-blocks (safety) are out of scope.
- **New or changed:** new blocks from a request, or changes to existing blocks of your project (`--edit`).
- **Versions:** every round is saved as its own folder (`v001`, `v002`, ...) with `generated.json` listing what was
  asked, what changed, and what TIA said. Any version can be imported again to roll back.
- **Fix loop:** TIA's import and compile messages go back to Claude with `--fix`, which writes the next version.
- **Review:** each version has a `REVIEW.md` for the engineer: what each block does, how to call it, assumptions, and
  what to check. Generated code must be reviewed before it goes to a real machine.

It runs in two places, because TIA (on the VM) has no internet and Claude (on the PC) has no TIA:

```
PC:  PlcKi.Server generate      request -> Claude -> package folder (v001), checked offline
VM:  TiaImporter import-blocks  package -> named TIA project -> compile -> import-report.json
PC:  PlcKi.Server generate --fix   report -> Claude -> v002     (repeat until it compiles)
```

## On the PC (PowerShell, in your PLC-Ki folder)

```powershell
git pull
# New blocks
dotnet run --project src\PlcKi.Server -- generate --out C:\temp\gen\valve --request "Make me a valve FB in LAD to our standard, with open and closed feedback, a monitoring time and a latched fault with reset. Also a tag table for XV201."

# Make them fit an existing program: give its export (TiaImporter export), Claude then reuses its tags and types
dotnet run --project src\PlcKi.Server -- generate --out C:\temp\gen\dosing --context C:\temp\demo-export --request "An FC that scales a raw analog value 0..27648 to engineering units, and use it for PT301"

# Change existing blocks of the program
dotnet run --project src\PlcKi.Server -- generate --out C:\temp\gen\valve-manual --context C:\temp\demo-export --edit FB_Valve --request "Add a manual mode: ManualOpen input, only when Manual is TRUE"

# A follow-up request on the same folder changes the latest version
dotnet run --project src\PlcKi.Server -- generate --out C:\temp\gen\valve --request "Add an Unused output that is TRUE when the valve has not moved for 24 h"
```

Your own rules: copy `knowledge\block-standard.md`, edit it, and pass `--standard C:\path\our-standard.md`.

## On the VM (Command Prompt)

Copy the package folder to the VM (e.g. `C:\PLCKi\gen\valve`). Close the project in TIA first, then:

```
cd C:\PLCKi\PLC-Ki\tools\TiaImporter
bin\Release\net48\TiaImporter.exe import-blocks --project C:\...\MixingDemo.ap20 --package C:\PLCKi\gen\valve
```

- Imports the latest version (or `--version v001` to go back to an earlier one).
- Refuses to overwrite anything that already exists in the project unless it is a block you asked to change with
  `--edit` (or you add `--replace`). Each block it replaces is first exported to `v00x\before\`.
- Saves the project only if everything imported and compiled without errors; otherwise the project on disk stays as
  it was. `--keep-on-error` saves anyway, to look at the errors in TIA.
- Writes `import-report.json` into the version folder. Nothing is downloaded to a PLC.

If it reports errors, copy the folder back to the PC and run
`dotnet run --project src\PlcKi.Server -- generate --out C:\temp\gen\valve --fix`, then import the new version.

## Lessons from the demo that are built in

What went wrong while bringing the demo into TIA V20 is now in Claude's instructions, the offline check or the importer:

| Lesson | Where |
| --- | --- |
| Openness refuses relative paths (.NET Framework `FileInfo`) | importer uses full paths for every file and the project |
| A GRAPH FB can't be a multi-instance (S7-1500) | prompt + offline check: own instance DB, called with it |
| GraphVersion 2.0 imports on the 1516 FW 1.8 | GRAPH reference file is the one that compiled |
| Instance DBs in one source with other blocks weren't found | one block per file, instance DB after its FB |
| `dt` is a keyword (DATE_AND_TIME); TIA refused the block | prompt + offline check for keyword names |
| `IN := NOT t.Q` timers don't pulse on S7-1500 | prompt + offline check; static Done flag instead |
| Uninitialized FB outputs give warnings | prompt: state in statics, copied to outputs |
| setup-demo stopped at the first refused file | import-blocks imports the rest and reports every refusal |
| Texts are en-US | the TIA project needs English as a project language |
| TIA asks to allow the exe after every rebuild | run `tools\TiaImporter\allow-openness.ps1` once after building |

## Live loop: one command from the PC

With `TiaImporter serve` running on the TIA PC, `develop` does the round trip by itself: Claude writes the blocks,
the TIA PC imports and compiles them, errors go back to Claude, and once it compiles it can download to PLCSIM and
watch the PLC. Every round is still a version in the package folder.

### Once, on the VM (Command Prompt as administrator)

```
netsh http add urlacl url=http://+:5090/ user=Software
netsh advfirewall firewall add rule name="PLC-Ki TiaImporter" dir=in action=allow protocol=TCP localport=5090
```

### Each session, on the VM (normal Command Prompt, TIA closed)

```
cd /d C:\PLCKi\PLC-Ki\tools\TiaImporter
bin\Release\net48\TiaImporter.exe serve --token <pick a password> --plcsim-ip 192.168.10.10
```

Leave the window open. Without `--plcsim-ip` it never downloads. With it, a download only happens when the PLC's
address in the project is that PLCSIM address and TIA reaches it through a PLCSIM virtual adapter. Software only,
never hardware, never a real PLC. Projects must be under `Documents\Automation`.

### On the PC (PowerShell)

```powershell
$env:PLCKI_VM_TOKEN = "<the same password>"
dotnet run --project src\PlcKi.Server -- develop --vm http://192.168.10.3:5090 `
  --project C:\Users\Software\Documents\Automation\MixingDemo_test\MixingDemo_test.ap20 `
  --out C:\temp\gen\pump --request "A pump FB in SCL with run feedback monitoring, and call it from a new cyclic OB with its own instance DB" `
  --download --plc-address 192.168.10.10 --watch 30
```

What it does: generate v001, upload, import and compile on the VM; on errors, up to 3 fix rounds (`--rounds`);
then download to PLCSIM and watch for 30 s (read-only): if the CPU leaves RUN or new alarms come up, they go back to
Claude as the next fix round. The blocks only run if something calls them, so ask for the call (an OB or a call in
an existing block via `--edit`) when you want the runtime check to mean something.

### Download to a real PLC (guarded)

Only after an engineer has reviewed the version. On the VM, list the real PLCs that may be loaded:

```
bin\Release\net48\TiaImporter.exe serve --token <password> --plcsim-ip 192.168.10.10 --allow-real 192.168.0.1
```

On the PC, use `--download-real` instead of `--download`. Once the version compiles, `develop` stops and asks you to
type the version name (for example `v003`) to confirm you reviewed it. Then the TiaImporter window on the TIA PC beeps
and asks for the PLC's address; nothing is downloaded unless someone there types it within 2 minutes.

- The CPU must already be in STOP. A running CPU is refused, never stopped. It stays in STOP afterwards: start it
  yourself once the machine is safe.
- Software only. Never through a PLCSIM adapter. No fix loop and no automatic retry after a real download.
- Every attempt is written to `C:\PLCKi\gen\real-downloads.log` (time, user, who asked, project, PLC, outcome).

## Not yet

- Reading the CPU diagnostic buffer (the watch sees the CPU state and active alarms, including system diagnostics
  alarms). Values of the new blocks are not checked against what they should do yet.
