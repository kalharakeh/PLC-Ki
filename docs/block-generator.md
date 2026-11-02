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

## Coming next

A live link between the PC and the VM, so the round trip runs without copying folders: import, compile, download to
PLCSIM only, then watch the PLC through the read-only live connection and the diagnostic buffer, and give Claude the
errors, CPU stops and misbehaving blocks to fix.
