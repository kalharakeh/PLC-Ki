# Siemens diagnostics knowledge base

`siemens-diagnostics.json` explains Siemens messages that do not come from the machine's own program:
CPU STOP reasons and diagnostic buffer events, error OBs (OB80, OB82, OB83, OB86, OB121, OB122), CPU LEDs,
PROFINET faults, module and channel diagnostics, fail-safe (F-I/O, PROFIsafe) messages, common SINAMICS
drive faults and communication errors.

The Ask assistant uses it through the `lookup_siemens_diagnostic` tool. It is general knowledge, so the
assistant always says so and points to the CPU diagnostic buffer to confirm.

The file is built into `PlcKi.Core` (embedded resource), so a change needs a rebuild.

## Adding an entry

Each entry has:

| Field | What goes in |
| --- | --- |
| `id` | Unique kebab-case id, prefixed by area (`cpu-`, `pn-`, `ch-`, `safety-`, `drive-`, `comm-`) |
| `category` | `CPU`, `PROFINET`, `Module`, `Safety`, `Drive` or `Communication` |
| `title` | Short name as people know it |
| `appliesTo` | CPU families or devices |
| `codes` | Exact identifiers: OB numbers, drive fault numbers (`F07900`), raw values (`32767`). Matched exactly; `F7900` finds `F07900` |
| `keywords` | Phrases from the diagnostic buffer, displays, or how people describe it. Matched as phrases, lower case |
| `meaning` | What it means, in plain English |
| `causes`, `checks`, `fixes` | Lists, most likely first |
| `cpuReaction` | Optional: what the CPU does (stays in RUN, goes to STOP) |
| `safety` | Optional: a safety warning for whoever does the fix |

`KnowledgeTests` checks every entry is complete and that typical questions find the right entry; add a
line there for a new entry.
