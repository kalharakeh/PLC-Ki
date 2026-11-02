# Demo alarm reference (answer key)

This is the expected answer for each alarm, used to check the troubleshooter's explanations.
All alarm bits are in `DB_Alarms`; they latch and clear with `DB_IO.PB_Reset` once the cause is gone.
FAULT alarms halt the sequence (outputs off, step kept). WARNING alarms only inform.

| Alarm | Raised in | Condition | Likely cause | Fix | Inject with `DB_FaultInjection` |
| --- | --- | --- | --- | --- | --- |
| A01 Emergency stop | FB_Station | `DB_IO.EStop_OK` = FALSE | E-stop button pressed | Release E-stop, Reset, Start | set `DB_IO.EStop_OK` FALSE |
| A02 XV101 did not open | FB_Valve `Station.XV101` | Open command on, open limit switch not reached in 10 s | No air, coil burnt, limit switch out of position | Check air supply and solenoid, check ZSO switch | `XV101_StuckClosed` |
| A03 XV101 did not close | FB_Valve | Command off, closed limit switch not reached in 10 s | Seat blocked, spring broken | Check valve seat, check ZSC switch | (none) |
| A04 XV102 did not open | FB_Valve `Station.XV102` | as A02 | as A02 | as A02 | (none) |
| A05 XV102 did not close | FB_Valve | as A03 | Seat blocked | Clean valve, check ZSC | `XV102_StuckOpen` |
| A06 M201 drive fault | FB_VFD `Station.M201` | Drive fault input on | Overcurrent, motor short, drive overheat | Read fault code on the drive, check motor, reset drive | `M201_DriveTrip` |
| A07 M201 speed deviation | FB_VFD | Running, speed < 90 % of setpoint for 15 s | Product too viscous, mechanical drag, drive current limit | Check product, gearbox, drive current limit | `M201_Overload` |
| A08 M201 not ready | FB_VFD | Drive not ready and no fault | E-stop, drive enable off, main switch off | Check E-stop and drive enable | set `DB_IO.EStop_OK` FALSE |
| A09 E301 overtemperature | FB_Station | Overtemp switch tripped (> 95 degC) | Temperature control failed | Let it cool, check TT301 and heater contactor | (heat with TempSP raised) |
| A10 P401 trip | FB_Motor `Station.P401` | Motor protection breaker tripped | Overload, blocked pump, phase loss | Check pump and motor, reset the breaker | `P401_Trip` |
| A11 P401 no feedback | FB_Motor | Command on, no running feedback in 5 s | Contactor coil, aux contact, wiring | Check contactor and its auxiliary contact | `P401_ContactorFault` |
| A12 LT101 signal fault | FB_AnalogIn `Station.LT101` | Raw value > 32511 | Cable broken, transmitter dead | Check wiring and 24 V supply of LT101 | `LT101_WireBreak` |
| A13 Level high-high | FB_AnalogIn | Level > 95 % | Inlet valve not closing, wrong recipe | Check XV101/XV102 close, check recipe | `XV102_StuckOpen` (eventually) |
| A14 TT301 signal fault | FB_AnalogIn `Station.TT301` | Raw value > 32511 | Cable broken | Check wiring of TT301 | `TT301_WireBreak` |
| A15 Temperature high | FB_AnalogIn | Temperature > 90 degC | Heater not switching off | Check heater contactor and TempSP | (none) |
| A16 Step 10 timeout | FB_BatchSequence | Fill A longer than 120 s | XV101 not open, supply tank empty | Check XV101 and ingredient A supply | `XV101_StuckClosed` (A02 comes first) |
| A17 Step 20 timeout | FB_BatchSequence | Fill B longer than 120 s | XV102 not open, supply empty | Check XV102 and ingredient B supply | (none) |
| A18 Step 30 timeout | FB_BatchSequence | Mix and heat longer than 600 s | Heater weak, TempSP too high | Check heater, recipe TempSP | `E301_HeaterFailed` (A20 comes first) |
| A19 Step 50 timeout | FB_BatchSequence | Drain longer than 180 s | Pump not delivering, outlet blocked | Check P401 and outlet line | (none) |
| A20 E301 no heating | FB_BatchSequence | Heater on 60 s, temperature rose < 2 degC | Heating element broken, heater fuse | Check heater element and fuses | `E301_HeaterFailed` |
| A21 Filler not ready | FB_BatchSequence | Step 40 waiting > 60 s for filler | Filler stopped or not in auto | Check the filler, signal `DB_LineInterface.Filler_ReadyToReceive` | `Filler_NeverReady` |
| A22 MES heartbeat lost | FB_Station | `MES_Heartbeat` unchanged for 5 s | MES down, network | Check MES connection | `MES_HeartbeatLost` |
| A23 Recipe invalid | FB_BatchSequence | Start pressed with implausible recipe | Wrong recipe values | Fix `DB_Recipe` (LevelA < LevelB <= 90, TempSP 20..85, speed 100..1500) | set `DB_Recipe.LevelA` > `LevelB` |
| A24 Not in Auto | FB_BatchSequence | Start pressed in Manual | Selector in Manual | Switch to Auto | set `DB_IO.SW_Auto` FALSE |
| A25 Tank not empty | FB_BatchSequence | Start pressed with level > 2 % | Previous batch not drained | Drain the tank | Start while a batch is half done after an abort |

Interlocks worth knowing (good test questions such as "why isn't the agitator running?"):

- M201 runs only with level above 20 % and E-stop OK.
- E301 heats only when M201 is at speed, no overtemperature, and TT301 healthy.
- XV101/XV102 open only when level is not high and LT101 is healthy.
- P401 runs only with level above 0.5 %.
