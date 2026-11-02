# Demo PLC program: mixing station

A batch mixing tank for a virtual **CPU 1516-3 PN/DP** in **PLCSIM Advanced**, with a built-in plant
simulation so levels, temperatures and speeds move by themselves. Used to test the PLC troubleshooter.

- `scl/` source files (data blocks and SCL blocks), generated into TIA in file-name order
- `lad/` LAD versions of the device blocks `FB_Valve`, `FB_Motor`, `FB_VFD` (SimaticML XML)
- `graph/` GRAPH version of the batch sequence `FB_BatchSequence` (SimaticML XML)
- `xml-gen/` the script that writes the XML, and an offline checker
- `alarms.md` every alarm with its cause and fix: the answer key for testing the app

With all three folders loaded the program uses all three languages, like a real plant:
devices in LAD, the sequence in GRAPH, analog scaling, alarms and the plant simulation in SCL.
See [LAD and GRAPH blocks](#lad-and-graph-blocks) below.

## The machine

| Tag | Equipment |
| --- | --- |
| XV101 / XV102 | Inlet valves ingredient A / B, with open and closed limit switches |
| M201 | Agitator on a VFD, speed in rpm |
| E301 | Heater with overtemperature switch |
| P401 | Outlet pump with running feedback and motor protection |
| LT101 / TT301 | Level 0..100 % / temperature 0..120 degC |

Sequence (`DB_BatchSequence.Step`): 0 Idle, 10 Fill A, 20 Fill B, 30 Mix and heat, 40 Hold (wait for filler),
50 Drain, 60 Done. Data exchange with "MES" and a downstream "filler" is in `DB_LineInterface`.

## Set it up in TIA Portal V20

1. **New project** with a **CPU 1516-3 PN/DP** (any recent firmware, e.g. V3.1).
   - Give PROFINET interface X1 an IP address in your VM's network, e.g. `192.168.0.10`.
   - Project properties > Protection: tick **Support simulation during block compilation** (PLCSIM needs it).
   - Nothing else: no OPC UA, no PUT/GET, no web server.
2. **Save and close** the project in TIA (Openness cannot open a project that is open elsewhere).
3. **Load the demo**, either way:
   - With the tool: `TiaImporter setup-demo --project C:\...\MixingDemo.ap20 --demo C:\...\plc-demo`
     (see `tools/TiaImporter/README.md` for building it). It generates the SCL blocks, then imports the LAD and
     GRAPH blocks over the SCL versions of the same blocks, compiles and saves.
   - By hand (SCL only): Project tree > PLC > External source files > Add new external file, select every file in `scl/`.
     Then right-click each file **in name order** (01 ... 40) > Generate blocks from source. Compile.
     TIA has no menu command to import the XML files in `lad/` and `graph/`, so by hand you get the all-SCL program.
4. **PLCSIM Advanced** on the VM: create an instance, communication **PLCSIM Virtual Eth. Adapter**
   (TCP/IP, not Softbus), same IP as in step 1, CPU type 1500. Power on.
   The VM's network adapter must be **bridged** so your desktop can reach that IP.
5. **Download** from TIA on your desktop to the instance, start the CPU.
   If TIA can go online from your desktop, the troubleshooter can too.

## Run a batch

Make a watch table with these tags and use "Modify":

| Tag | Use |
| --- | --- |
| `DB_IO.PB_Start` | set TRUE, then back to FALSE, to start (or continue after a Stop / fault) |
| `DB_IO.PB_Stop` | pause the batch |
| `DB_IO.PB_Reset` | reset alarms (TRUE then FALSE) |
| `DB_IO.SW_Auto`, `DB_IO.EStop_OK` | TRUE by default |
| `DB_BatchSequence.Step`, `DB_BatchSequence.StepTime` | where the sequence is |
| `Station.LT101.Value`, `Station.TT301.Value`, `DB_IO.M201_SpeedPV` | level, temperature, speed |
| `DB_Alarms.*` | alarm bits |

A full batch takes about 4 minutes. To test troubleshooting, set any bit in `DB_FaultInjection` to TRUE
and ask the app what is wrong; `alarms.md` lists the expected answers.

## Then: one test export

Archive the project (Project > Archive, gives a `.zap20`), then run
`TiaImporter export --archive MixingDemo.zap20 --out C:\temp\demo-export`.
Doing the same with one of your real plant archives (LAD + GRAPH) and sharing the export folder lets me
build the LAD/GRAPH parser on real V20 output.

## LAD and GRAPH blocks

The XML blocks replace SCL blocks of the same name and keep their inputs and outputs. The SCL versions stay in
`scl/` as a fallback. The sequence runs in its own instance DB `DB_BatchSequence` (not inside `Station`), because
TIA does not allow a GRAPH block as a multi-instance on this CPU; the SCL version uses the same DB.

| Block | Language | Number | What it shows |
| --- | --- | --- | --- |
| `FB_Valve` | LAD | FB101 | Contacts in series, normally closed contacts, TON timers for travel monitoring |
| `FB_Motor` | LAD | FB102 | Interlock contacts, running feedback, TON feedback monitoring |
| `FB_VFD` | LAD | FB103 | MOVE and MUL boxes, a comparator in the rung, a temp variable |
| `FB_BatchSequence` | GRAPH | FB104 | 7 steps, LAD transitions, N actions, step interlocks, LAD pre- and post-operations |

### The GRAPH sequence

| GRAPH step | `Step` output | Actions (N) | Leaves when (transition) |
| --- | --- | --- | --- |
| 1 Idle (initial) | 0 | none | T1 `StartBatch`: not halted, Start, recipe valid, level <= DrainLevel |
| 10 FillA | 10 | OpenXV101 | T2: not halted, level >= LevelA |
| 20 FillB | 20 | OpenXV102 | T3: not halted, level >= LevelB |
| 30 MixHeat | 30 | RunM201, HeatReq | T4: not halted, temperature >= TempSP and `MixHeat.U` >= MixTime |
| 40 Hold | 40 | RunM201 | T5: not halted, FillerReady |
| 50 Drain | 50 | RunP401 | T6: not halted, level <= DrainLevel |
| 60 Done | 60 | BatchDone | T7: always (jump back to Idle) |

- **Halting** is done the GRAPH way: steps 10 to 50 have the interlock `Healthy` (no fault, E-stop OK, not paused,
  in Auto) and their actions are interlocked. On a fault the step is kept, its actions switch off and GRAPH reports
  an interlock error ("Sequence halted: fault, E-stop, Stop or not in Auto"). It continues as soon as the cause is
  gone, exactly like the SCL version: Reset for a latched alarm, Start after a Stop.
- **Pre-operations** (LAD, before the sequence): recipe check, Stop/Start pause latch, `Healthy` and `Halted`,
  temperature at the start of step 30.
- **Post-operations** (LAD, after the sequence): `Step` and `StepTime` from the active step, agitator speed,
  heater (`HeatReq` and below TempSP), step timeouts, filler wait, no-heating check, start checks.
- `StepTime` is the GRAPH step time without interlock error (`<step>.U`), so it **pauses** while halted and
  continues afterwards. The SCL version restarted it from zero. Step timeouts use the same time.
- No GRAPH supervisions are used; timeouts are raised as alarms by `FB_AlarmHandler` like before.

### If the import fails

`TiaImporter setup-demo` stops at the first block TIA refuses and does not save the project. The message names
the file and the reason. Two likely ones, both fixed by re-running the generator (needs Python 3):

- **Culture / language not available**: the XML texts are English (United States). Either add it under
  Project tree > Languages & resources > Project languages, or run
  `python xml-gen/make_demo_xml.py --culture de-DE` (your project language).
- **GRAPH version or `G7_..._V2` types not supported**: run
  `python xml-gen/make_demo_xml.py --graph-version 6.0` (or the GRAPH version TIA offers in the block properties
  of a new GRAPH block for your CPU).

To get going while something is being fixed, move the refused XML file out of `lad/` or `graph/` and run
`setup-demo` again: that block then stays in SCL. `python xml-gen/check_demo_xml.py` checks the XML offline
(wiring, operands, interfaces) but only TIA can confirm it imports and compiles.
