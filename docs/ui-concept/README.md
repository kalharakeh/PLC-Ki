# PLC-Ki UI concept

Clickable mockup: open [`index.html`](index.html) in a browser (no server needed). It uses the demo mixing
station (`plc-demo/scl`) in three switchable states: **Mixing** (normal, waiting for temperature), **E-stop** (A01 with follow-up A08),
**Valve fault** (A02 XV101 did not open, batch halted) and **Waiting** (step 40, filler not ready, warning A21).
Values are simulated and tick every second. It opens on the **Active alarm** page in the Valve fault scenario.

This is a design proposal only. It does not touch `src/PlcKi.Server` or `tools/TiaImporter`.

## The main rule: generate every view from the program

PLC-Ki does not know the plant before it reads the archive, so no screen is drawn by hand. Every view is
built from the machine model that the importer produces, and live values are placed on top of it.
Each view header says what it was built from (for example "Built from OB_Main call tree → FB_Station with
7 device instances"), so a technician can trust it, or see why something is missing.

| Source in the archive | What PLC-Ki builds from it |
| --- | --- |
| OB call tree, FB multi-instances, instance comments | Plant overview, equipment list, units |
| FB interface (inputs, outputs, statics + comments) | Faceplates, one per block type |
| GRAPH blocks (steps, transitions, actions, supervisions) | Step charts |
| SCL `CASE #Step OF` patterns | The same step charts for sequences written in SCL |
| LAD / FBD networks (SimaticML FlgNet) | Live rung view |
| SCL statements | Code view with live values; rungs drawn from boolean assignments |
| Alarm DBs, ProDiag / Program_Alarm, where each bit is set | Alarm list linked to code, cause and fix |
| Comments on DBs like `DB_LineInterface` ("From MES:", "To filler:") | Data exchange partners and directions |
| Any tag in the model | Trends |

Claude fills the gaps **once, at import time**: it names and groups equipment from symbols and comments,
classifies device blocks (valve, motor, VFD, analog input...) by their interface, writes the plain-English
block summaries, and drafts cause and fix text for each alarm. The results are stored in the model, so the
live screens cost nothing and work without network access. Live questions go to the Ask panel.

When something cannot be recognised, the view falls back to a generic form instead of guessing:
an unknown FB becomes a "device" with its inputs and outputs listed; a block with no detectable steps shows
only its code view.

## Unknown programs and several stations

The mixing station is only the example. For a program we have never seen:
1. **Stations:** each large FB instance called from OB1 (or from a line FB) becomes a station (Mixing, Sorting,
   Filling...). Claude names and groups them from instance names, comments, symbol prefixes and the hardware
   config (ET 200 / PROFINET device names).
2. **Equipment:** FB instances inside a station are classified by interface (open/closed switches → valve,
   run + feedback → motor, speed setpoint → drive...). A block library (customer or Siemens LBP) is learned once.
3. **Pictures:** reuse the WinCC screens when the HMI project is in the archive; else a template when the
   pattern is clear (tank, conveyor); else a station card with device tiles and the step strip.
4. **Several stations:** Plant shows one card per station (state, step, alarms) with arrows for the signals
   one station writes and another reads; clicking a station opens the same views scoped to it. Active alarm
   names the station.
5. **Import review:** after import a short page lists what was found ("4 stations, 63 devices, 3 sequences,
   5 blocks not recognised"); names and groupings are corrected once and saved with the model.

## Additions (2 Nov, second round)

All of these are in the mockup. The new **E-stop** scenario shows the alarm chain.

1. **Where to measure.** Every signal on the Active alarm page and in each faceplate shows its I/O address,
   module, slot, channel, terminal and cabinet (from the hardware config and the tag table), or the
   PROFINET device and telegram word for drives. The electrician knows where to put the meter.
2. **First alarm first.** Active alarms are sorted root first. The root alarm gets a "1ST" badge, and
   alarms it caused are folded under it with "came 1 s later; clears with the same fix" (E-stop A01 →
   drive not ready A08). Links between alarms come from the code (A08's condition depends on the E-stop
   signal) and from timing.
3. **Fix notes.** Under "How to fix", a "Fixed before" list shows what technicians wrote last time
   ("solenoid coil Y101 burnt, spares in store B3"), and a one-line box adds a new note.
4. **Shift handover.** A new Shift view: a timeline of alarms, batches, mode changes, forces and
   downloads since shift start, with a short Claude summary and key figures.
5. **Downtime ranking.** In the same view: alarms ranked by stopped time, not by count; split by station
   when there are several.
6. **Forces and test bits.** A banner on every page when I/O is forced (if the CPU reports it) or a test bit
   such as `DB_FaultInjection` is on, so a simulated fault is never mistaken for a real one.
7. **Program changed.** The top bar shows "Program = archive" or "Program changed". When the blocks in the
   PLC differ from the imported archive, a banner names them and asks for a new import, because
   explanations for those blocks may be out of date.
8. **Shop floor modes.** A TV button for a large screen on the line (no menu, big text, opens Active alarm
   or Plant), bigger touch targets on tablets, and a language switch (EN/DE in the mockup; generated texts
   are translated by Claude at import).

## Layout

- **Top bar:** machine, CPU and archive, connection state, the Read-only badge, clock, and the **Ask** button.
- **Left rail:** Active alarm (the start page), Plant, Sequence, Logic, Code, Alarms, Trends, Equipment. The Alarms entry shows the count of
  active alarms in red (fault) or amber (warning). On a phone the rail becomes a row of tabs.
- **Ask panel:** slides in from the right over any view. Answers link to the view that proves them
  ("Show the rung", "Alarm details"). The real chat is being built in the status page; the mockup only shows
  where it sits and how answers link back.
- Everything is cross-linked: a step links to its code and trend, an alarm to its rung, code and device,
  a blocked contact to the reason it is blocked.

## The views

### 0. Active alarm (start page)
kassem's team opens an "Active Alarm" screen first on a breakdown, so this is the landing page. It puts
everything needed to fix the top alarm on one screen (faults before warnings, a chip row when several are active):
- **The alarm:** number, text, fault or warning, since when, acknowledged or not, and a few plain sentences on
  **what is going on** (what the machine was doing, what failed, what it means for the batch).
- **Where in the machine:** the process picture with the device marked by a pulsing ring, plus the device's
  "why it works or not" chain (or, for sequence alarms, the condition the step is waiting for).
- **Where in the software:** the path from OB to the alarm bit and the live rung that sets it, with the
  values at alarm time when the rung looks normal again now.
- **Values:** every signal involved, at alarm time and now; changed values are highlighted. Offline (no
  PLC connection) the page shows the last values read and how old they are.
- **Likely causes, how to fix, history,** and "Ask about this alarm".
With no alarm active, it says so and shows what the machine is doing, with a link to the last alarm.

### 1. Plant
The first screen answers "what is it doing right now?" in one or two plain sentences, next to a big
state box (step number + Running / Waiting / Halted). Below: the sequence as a strip of steps, the
"waiting for" sentence, one tile per device (icon by type, colour by state, key value), active alarms and
data exchange with MES and the filler. The device tiles come from the FB instances in `FB_Station`.
E301 has no device block; the importer found it from its I/O tag names, and the tile says so.
The **process picture** (tank with valves, agitator, heater, transmitters and pump, live level and flows) is
drawn from a "batch tank" template that the importer applies when it recognises the pattern (a vessel with
a level transmitter, inlet valves and an outlet pump). Each device in it is clickable. Plants it doesn't
recognise get the device tiles only.

### 2. Sequence (GRAPH step chart)
A vertical step chart in GRAPH style: initial step with a double frame, active step filled blue (red when
halted), the live transition red while it blocks and green when it would fire. Clicking a step shows:
- **Waiting for:** one sentence, with an estimate when a value is trending ("about 1:11 to go").
- **Time in step** against the supervision limit and the alarm it would raise.
- **Actions** (N, C, P qualifiers) with on/off.
- **Transition** conditions, one row each, with the symbol and the live comparison (`53.6 °C ≥ 60.0 °C`).
- When halted, a banner shows the halt conditions (OR) and which one is true.

For real GRAPH blocks all of this comes straight from the GRAPH XML; for the SCL demo it comes from the
`CASE #StepNo OF` branches.

### 3. Logic (LAD rung view)
A list of networks that drive outputs, alarms and conditions, and a live rung for the selected one. Green
wire carries power; the first contact that stops it is marked red with "← blocks", and a link asks the
next question ("Why is that FALSE?") by jumping to the network or view that drives it. A passing OR branch
does not mark its open contacts as blockers. For alarms that came and went, an "At alarm time" note shows
the values captured at the alarm edge, because the live rung may look normal again (the A02 case).
Real LAD/FBD networks are drawn as TIA draws them; SCL boolean assignments are drawn as rungs too.

### 4. Code (SCL with live values)
Call tree on the left with language tags (LAD, FBD, SCL, GRAPH, STL). On the right: Claude's plain-English
summary of the block, then the source with a live value chip after each operand (green TRUE, red FALSE,
blue numbers). Lines that run in the current step are highlighted. Multi-instance blocks show values of
the chosen instance (FB_Valve shows `"Station".XV101`).

### 5. Alarms
Active alarms on top of a history kept by PLC-Ki itself (it sees every alarm edge while polling, so no HMI
or alarm server is needed). The detail panel for an alarm:
- **Why it is on now:** the condition rows with live values.
- **Values when it came in:** snapshot taken at the rising edge.
- **Raised in:** the path from OB to the alarm bit, with buttons to the logic, the code and the device.
- **Likely causes,** ranked, each with the evidence ("closed switch still on, so the valve did not move").
- **How to fix,** including how to restart the sequence.
- **History:** occurrences per day for 14 days. Plant staff can add notes that stay with the alarm.

### 6. Trends
Stacked small charts (level, temperature, speed), not one chart with three axes, sharing a time axis.
Steps run along the top so each curve reads against the sequence; digital commands show as bars; alarms
are marked where they came in; setpoints are dashed lines. Point at the chart to read all values at that
time. Time ranges include "This batch" and "Compare with the previous batch".
PLC-Ki records the trend data itself while it runs.

### 7. Equipment (faceplates)
One faceplate layout per block type (valve, VFD, motor, analog input, heater found from I/O), so a plant
with 200 valves needs no extra work. Each faceplate shows the main value, status lamps, the alarms of the
device and a **"Why it runs or not" chain**: request from the sequence → permit/interlock → ready →
command out → feedback. The first broken link is red, which answers "why isn't the agitator running?"
without reading code.

## Visual language

- A dark navy app frame (top bar and rail) around light content cards, like a control-room application;
  content colours continue the current status page (`src/PlcKi.Server/wwwroot/style.css`), with a dark theme. Green = on / passing, red = fault / blocking, amber = warning / waiting, blue = active step and
  links, grey = off. State is always shown by shape or text as well as colour.
- IBM Plex Sans for text and IBM Plex Mono for tags, symbols and numbers (tabular figures), with system
  fonts as fallback for plant PCs without internet.
- Plain English first, TIA symbol second (smaller, monospace), so both operators and technicians are served.
- No write controls anywhere. The Read-only badge stays visible on every screen.

## Build order (proposal)

1. Shell (rail, top bar, routing), **Active alarm**, **Plant** and **Alarms** on top of the existing
   `/api/status`: highest value, needs only the hand-written model.
2. **Sequence** from SCL CASE patterns (demo), then from GRAPH XML once a real export arrives.
3. **Equipment** faceplates by block type, and alarm history + trend recording (small local store, e.g. SQLite).
4. **Code** view with live chips (needs block sources from the importer).
5. **Logic** rung view from LAD/FBD XML (needs the real V20 export to build the parser on).
6. Ask panel joins in as the chat lands; answers link to these views.

Decided with kassem (2 Nov): operators may see every view, including Code and Logic (no role switch).
The start page is Active alarm.
