# Demo PLC program: mixing station

A batch mixing tank for a virtual **CPU 1516-3 PN/DP** in **PLCSIM Advanced**, with a built-in plant
simulation so levels, temperatures and speeds move by themselves. Used to test the PLC troubleshooter.

- `scl/` source files (data blocks and SCL blocks), generated into TIA in file-name order
- `alarms.md` every alarm with its cause and fix: the answer key for testing the app
- `lad/`, `graph/` LAD and GRAPH versions of the device blocks and sequence (coming next)

## The machine

| Tag | Equipment |
| --- | --- |
| XV101 / XV102 | Inlet valves ingredient A / B, with open and closed limit switches |
| M201 | Agitator on a VFD, speed in rpm |
| E301 | Heater with overtemperature switch |
| P401 | Outlet pump with running feedback and motor protection |
| LT101 / TT301 | Level 0..100 % / temperature 0..120 degC |

Sequence (`Station.Seq.Step`): 0 Idle, 10 Fill A, 20 Fill B, 30 Mix and heat, 40 Hold (wait for filler),
50 Drain, 60 Done. Data exchange with "MES" and a downstream "filler" is in `DB_LineInterface`.

## Set it up in TIA Portal V20

1. **New project** with a **CPU 1516-3 PN/DP** (any recent firmware, e.g. V3.1).
   - Give PROFINET interface X1 an IP address in your VM's network, e.g. `192.168.0.10`.
   - Project properties > Protection: tick **Support simulation during block compilation** (PLCSIM needs it).
   - Nothing else: no OPC UA, no PUT/GET, no web server.
2. **Save and close** the project in TIA (Openness cannot open a project that is open elsewhere).
3. **Load the demo**, either way:
   - With the tool: `TiaImporter setup-demo --project C:\...\MixingDemo.ap20 --demo C:\...\plc-demo`
     (see `tools/TiaImporter/README.md` for building it). It generates all blocks, compiles and saves.
   - By hand: Project tree > PLC > External source files > Add new external file, select every file in `scl/`.
     Then right-click each file **in name order** (01 ... 40) > Generate blocks from source. Compile.
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
| `Station.Seq.Step`, `Station.Seq.StepTime` | where the sequence is |
| `Station.LT101.Value`, `Station.TT301.Value`, `DB_IO.M201_SpeedPV` | level, temperature, speed |
| `DB_Alarms.*` | alarm bits |

A full batch takes about 4 minutes. To test troubleshooting, set any bit in `DB_FaultInjection` to TRUE
and ask the app what is wrong; `alarms.md` lists the expected answers.

## Then: one test export

Archive the project (Project > Archive, gives a `.zap20`), then run
`TiaImporter export --archive MixingDemo.zap20 --out C:\temp\demo-export`.
Doing the same with one of your real plant archives (LAD + GRAPH) and sharing the export folder lets me
build the LAD/GRAPH parser on real V20 output.
