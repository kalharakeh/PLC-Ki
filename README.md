# PLC Troubleshooter

A web app that connects read-only to Siemens S7-1500 / S7-1200 PLCs, decodes the program from the TIA Portal
archive (.zap), and explains in plain English what the machine is doing and why an alarm is on.

Plan and architecture: https://claude.ai/artifact/9CgfxZD1ehAAZ6yVGEvczf

| Folder | What |
| --- | --- |
| `plc-demo/` | Demo PLC program (mixing station) for PLCSIM Advanced, with fault injection and an alarm answer key |
| `tools/TiaImporter/` | TIA Openness tool: export a .zap20 archive to XML, load the demo into a project |
| `server/` | Web app (coming in phase 2) |

**Safety:** the app only reads from PLCs. There is no code path that writes to a PLC.
