# Block standard (default)

The block generator follows this file unless you pass your own with `--standard <file>`. Copy it, change it to your
plant's rules, and keep it next to your projects. Plain words are fine; Claude reads it as instructions.

## Names
- Blocks: `FB_<Device>` for device blocks (FB_Valve, FB_Motor), `FC_<Verb><Thing>` for functions (FC_ScaleAnalog),
  `DB_<Unit>` for global and instance DBs, `UDT_<Thing>` for PLC data types.
- Interface members: PascalCase, English. Commands start with a verb (Open, Start, Reset), states describe the state
  (IsOpen, Running), faults start with Fault (FaultNotOpen).
- Tags: `<plant tag>_<signal>`, e.g. `XV101_ZSO`, `M201_Run`, `PT301_Value`. Tag tables per area or device type.
- Instances: one instance DB per sequence and per top-level unit; devices as multi-instances inside the unit FB.

## Device FB interface
- Inputs: commands (Bool), Permit (Bool, default TRUE), feedbacks, Reset, monitoring times (Time).
- Outputs: drive outputs (Out, Run, Speed setpoint), states (IsOpen, Running), one Bool per fault, plus Fault (any).
- Faults latch until Reset; outputs go to the safe state while a fault is active.

## Code
- SCL for calculations, scaling and data handling; LAD for device logic and interlocks; GRAPH for step sequences.
- One network per function in LAD with a title and a comment.
- Comments in English.
