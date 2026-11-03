#!/usr/bin/env python3
"""Writes the LAD and GRAPH blocks of the demo mixing station as SimaticML XML for TIA Portal V20.

    python make_demo_xml.py                     # writes ../lad/*.xml and ../graph/*.xml
    python make_demo_xml.py --graph-version 6.0 # GRAPH block for an older GRAPH runtime (see README)
    python make_demo_xml.py --culture de-DE     # if the TIA project has no English (United States)

The XML is committed, so this script is only needed to change the blocks. The structure follows real
TIA V18/V20 exports (LAD FlgNet v5, GRAPH v5). TiaImporter setup-demo imports the files with
ImportOptions.Override, so they replace the SCL versions of the same blocks.
"""
import argparse
import os
import re
from xml.sax.saxutils import escape

NS_IF = "http://www.siemens.com/automation/Openness/SW/Interface/v5"
NS_FLG = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"
NS_GRAPH = "http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v5"

CULTURE = "en-US"


def attr(s):
    return escape(str(s), {'"': "&quot;"})


# --------------------------------------------------------------------------------------------
# Operands
# --------------------------------------------------------------------------------------------

def access_xml(op, uid):
    """'#A.B' local, '"DB".A' global, 'T#5S' typed constant, 'real:0.9' / 'int:10' / 'bool:TRUE' literal."""
    if op.startswith("#"):
        comps = op[1:].split(".")
        inner = "".join(f'<Component Name="{attr(c)}" />' for c in comps)
        return f'<Access Scope="LocalVariable" UId="{uid}"><Symbol>{inner}</Symbol></Access>'
    if op.startswith('"'):
        m = re.match(r'^"([^"]+)"((?:\.[A-Za-z_][A-Za-z0-9_]*)*)$', op)
        if not m:
            raise ValueError(f"bad global operand {op}")
        comps = [m.group(1)] + [c for c in m.group(2).split(".") if c]
        inner = "".join(f'<Component Name="{attr(c)}" />' for c in comps)
        return f'<Access Scope="GlobalVariable" UId="{uid}"><Symbol>{inner}</Symbol></Access>'
    if op.upper().startswith("T#"):
        return (f'<Access Scope="TypedConstant" UId="{uid}"><Constant>'
                f'<ConstantValue>{attr(op)}</ConstantValue></Constant></Access>')
    if ":" in op:
        typ, val = op.split(":", 1)
        typ = {"real": "Real", "int": "Int", "bool": "Bool"}[typ.lower()]
        return (f'<Access Scope="LiteralConstant" UId="{uid}"><Constant><ConstantType>{typ}</ConstantType>'
                f'<ConstantValue>{attr(val)}</ConstantValue></Constant></Access>')
    raise ValueError(f"unknown operand {op}")


# --------------------------------------------------------------------------------------------
# One LAD network (FlgNet)
#
# A rung is a list of series elements from the power rail, then one or more end elements that
# all hang off the end of the series chain (a parallel fan-out, as TIA exports it).
#   ("C", op)                      normally open contact
#   ("CN", op)                     normally closed contact
#   ("CMP", "Gt", "Real", a, b)    comparator box in the rung (Gt Ge Lt Le Eq Ne)
# End elements:
#   ("COIL", op) ("S", op) ("R", op)
#   ("MOVE", src, dst)
#   ("CALC", "Add"|"Sub"|"Mul", "Real", a, b, dst)
#   ("TON", instance, pt, q_dst)   IEC timer multi-instance, Q to a coil
#   ("TRCOIL",) ("ILCOIL",) ("SVCOIL",)   GRAPH transition / interlock / supervision coil
# --------------------------------------------------------------------------------------------

class Net:
    def __init__(self):
        self.items = []   # ("access", key, op) | ("part", key, xml-fn)
        self.wires = []   # (src, [dst...]); src/dst: ("rail",) ("name", key, pin) ("ident", key) ("open",)
        self._k = 0

    def _key(self):
        self._k += 1
        return self._k

    def acc(self, op):
        k = self._key()
        self.items.append(("access", k, op))
        return k

    def part(self, fn):
        k = self._key()
        self.items.append(("part", k, fn))
        return k

    def wire(self, src, *dst):
        self.wires.append((src, list(dst)))

    def render(self, xmlns=True):
        uid = {}
        n = 21
        accesses = [i for i in self.items if i[0] == "access"]
        parts = [i for i in self.items if i[0] == "part"]
        for _, k, _ in accesses + parts:
            uid[k] = n
            n += 1
        out = []
        for _, k, op in accesses:
            out.append(access_xml(op, uid[k]))
        part_xml = []
        for _, k, fn in parts:
            xml, extra = fn(uid[k], n)
            n += extra
            part_xml.append(xml)
        out += part_xml

        def con(c):
            nonlocal n
            if c[0] == "rail":
                return "<Powerrail />"
            if c[0] == "name":
                return f'<NameCon UId="{uid[c[1]]}" Name="{c[2]}" />'
            if c[0] == "ident":
                return f'<IdentCon UId="{uid[c[1]]}" />'
            if c[0] == "open":
                n += 1
                return f'<OpenCon UId="{n - 1}" />'
            raise ValueError(c)

        wires = []
        for src, dst in self.wires:
            wid = n
            n += 1
            body = con(src) + "".join(con(d) for d in dst)
            wires.append(f'<Wire UId="{wid}">{body}</Wire>')
        ns = f' xmlns="{NS_FLG}"' if xmlns else ""
        return f"<FlgNet{ns}><Parts>{''.join(out)}</Parts><Wires>{''.join(wires)}</Wires></FlgNet>"


def simple_part(name, *, negated=False, templates=(), version=None):
    def fn(u, _n):
        v = f' Version="{version}"' if version else ""
        inner = ""
        if negated:
            inner += '<Negated Name="operand" />'
        for tname, ttype, tval in templates:
            inner += f'<TemplateValue Name="{tname}" Type="{ttype}">{tval}</TemplateValue>'
        if inner:
            return f'<Part Name="{name}"{v} UId="{u}">{inner}</Part>', 0
        return f'<Part Name="{name}"{v} UId="{u}" />', 0
    return fn


def timer_part(kind, instance):
    def fn(u, n):
        xml = (f'<Part Name="{kind}" Version="1.0" UId="{u}">'
               f'<Instance Scope="LocalVariable" UId="{n}"><Component Name="{attr(instance)}" /></Instance>'
               f'<TemplateValue Name="time_type" Type="Type">Time</TemplateValue></Part>')
        return xml, 1   # the instance takes one UId
    return fn


def rung(series, ends):
    net = Net()
    prev = ("rail",)
    for el in series:
        kind = el[0]
        if kind in ("C", "CN"):
            a = net.acc(el[1])
            p = net.part(simple_part("Contact", negated=(kind == "CN")))
            net.wire(prev, ("name", p, "in"))
            net.wire(("ident", a), ("name", p, "operand"))
            prev = ("name", p, "out")
        elif kind == "CMP":
            _, op, typ, x, y = el
            ax, ay = net.acc(x), net.acc(y)
            p = net.part(simple_part(op, templates=[("SrcType", "Type", typ)]))
            net.wire(prev, ("name", p, "pre"))
            net.wire(("ident", ax), ("name", p, "in1"))
            net.wire(("ident", ay), ("name", p, "in2"))
            prev = ("name", p, "out")
        else:
            raise ValueError(el)

    # End elements: create parts first, then one fan-out wire from the chain end to all inputs.
    inputs, later = [], []
    for el in ends:
        kind = el[0]
        if kind in ("COIL", "S", "R"):
            a = net.acc(el[1])
            p = net.part(simple_part({"COIL": "Coil", "S": "SCoil", "R": "RCoil"}[kind]))
            inputs.append(("name", p, "in"))
            later.append((("ident", a), ("name", p, "operand")))
        elif kind in ("TRCOIL", "ILCOIL", "SVCOIL"):
            p = net.part(simple_part({"TRCOIL": "TrCoil", "ILCOIL": "IlCoil", "SVCOIL": "SvCoil"}[kind]))
            inputs.append(("name", p, "in"))
        elif kind == "MOVE":
            _, src, dst = el
            a_in, a_out = net.acc(src), net.acc(dst)
            p = net.part(simple_part("Move", templates=[("Card", "Cardinality", "1")]))
            inputs.append(("name", p, "en"))
            later.append((("ident", a_in), ("name", p, "in")))
            later.append((("name", p, "out1"), ("ident", a_out)))
        elif kind == "CALC":
            _, op, typ, x, y, dst = el
            ax, ay, ad = net.acc(x), net.acc(y), net.acc(dst)
            # Add and Mul take extra inputs (Card); Sub and Div have exactly two and no Card template
            tpl = [("Card", "Cardinality", "2")] if op in ("Add", "Mul") else []
            p = net.part(simple_part(op, templates=tpl + [("SrcType", "Type", typ)]))
            inputs.append(("name", p, "en"))
            later.append((("ident", ax), ("name", p, "in1")))
            later.append((("ident", ay), ("name", p, "in2")))
            later.append((("name", p, "out"), ("ident", ad)))
        elif kind == "TON":
            _, inst, pt, q = el
            a_pt, a_q = net.acc(pt), net.acc(q)
            t = net.part(timer_part("TON", inst))
            c = net.part(simple_part("Coil"))
            inputs.append(("name", t, "IN"))
            later.append((("ident", a_pt), ("name", t, "PT")))
            later.append((("name", t, "Q"), ("name", c, "in")))
            later.append((("name", t, "ET"), ("open",)))
            later.append((("ident", a_q), ("name", c, "operand")))
        else:
            raise ValueError(el)
    net.wire(prev, *inputs)
    for src, dst in later:
        net.wire(src, dst)
    return net


# --------------------------------------------------------------------------------------------
# Block documents
# --------------------------------------------------------------------------------------------

class Ids:
    def __init__(self):
        self.n = 0

    def next(self):
        self.n += 1
        return format(self.n, "X")


def mtext(ids, composition, text):
    t = f"<Text>{escape(text)}</Text>" if text else "<Text />"
    return (f'<MultilingualText ID="{ids.next()}" CompositionName="{composition}"><ObjectList>'
            f'<MultilingualTextItem ID="{ids.next()}" CompositionName="Items"><AttributeList>'
            f"<Culture>{CULTURE}</Culture>{t}</AttributeList></MultilingualTextItem></ObjectList></MultilingualText>")


def member(name, dtype, comment=None, start=None, version=None):
    v = f' Version="{version}"' if version else ""
    inner = ""
    if comment:
        inner += f'<Comment><MultiLanguageText Lang="{CULTURE}">{escape(comment)}</MultiLanguageText></Comment>'
    if start is not None:
        inner += f"<StartValue>{escape(start)}</StartValue>"
    if inner:
        return f'<Member Name="{attr(name)}" Datatype="{attr(dtype)}"{v}>{inner}</Member>'
    return f'<Member Name="{attr(name)}" Datatype="{attr(dtype)}"{v} />'


def section(name, members):
    if not members:
        return f'<Section Name="{name}" />'
    return f'<Section Name="{name}">' + "".join(members) + "</Section>"


def lad_fb(name, number, title, comment, iface, networks):
    """iface: dict section -> list of member xml. networks: list of (title, comment, Net)."""
    ids = Ids()
    sections = "".join(section(s, iface.get(s, [])) for s in ("Input", "Output", "InOut", "Static", "Temp", "Constant"))
    block_comment = mtext(ids, "Comment", comment)
    units = []
    for ntitle, ncomment, net in networks:
        uid = ids.next()
        c = mtext(ids, "Comment", ncomment)
        t = mtext(ids, "Title", ntitle)
        units.append(
            f'<SW.Blocks.CompileUnit ID="{uid}" CompositionName="CompileUnits"><AttributeList>'
            f"<NetworkSource>{net.render(xmlns=True)}</NetworkSource>"
            f"<ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>"
            f"<ObjectList>{c}{t}</ObjectList></SW.Blocks.CompileUnit>")
    block_title = mtext(ids, "Title", title)
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n<Document><Engineering version="V20" />'
        '<SW.Blocks.FB ID="0"><AttributeList>'
        "<AutoNumber>false</AutoNumber><HeaderAuthor>Demo</HeaderAuthor><HeaderFamily /><HeaderName />"
        "<HeaderVersion>0.2</HeaderVersion>"
        f'<Interface><Sections xmlns="{NS_IF}">{sections}</Sections></Interface>'
        "<MemoryLayout>Optimized</MemoryLayout>"
        f"<Name>{name}</Name><Namespace /><Number>{number}</Number>"
        "<ProgrammingLanguage>LAD</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>"
        f"</AttributeList><ObjectList>{block_comment}{''.join(units)}{block_title}</ObjectList>"
        "</SW.Blocks.FB></Document>\n")


# --------------------------------------------------------------------------------------------
# LAD device blocks (same names and interfaces as the SCL versions, so FB_Station is unchanged)
# --------------------------------------------------------------------------------------------

def fb_valve():
    iface = {
        "Input": [
            member("Open", "Bool", "Request: open the valve"),
            member("Permit", "Bool", "Interlock: valve may only open when TRUE", "true"),
            member("ZSO", "Bool", "Open limit switch"),
            member("ZSC", "Bool", "Closed limit switch"),
            member("MonitorTime", "Time", "Max travel time before a fault", "T#10S"),
        ],
        "Output": [
            member("Out", "Bool", "Open command to the solenoid"),
            member("IsOpen", "Bool", "Valve confirmed open"),
            member("IsClosed", "Bool", "Valve confirmed closed"),
            member("FaultNotOpen", "Bool", "Open command on, but open limit switch not reached in MonitorTime"),
            member("FaultNotClosed", "Bool", "Open command off, but closed limit switch not reached in MonitorTime"),
        ],
        "Static": [
            member("tOpen", "TON_TIME", "Opening travel time", version="1.0"),
            member("tClose", "TON_TIME", "Closing travel time", version="1.0"),
        ],
    }
    nets = [
        ("Open command", "The solenoid gets the open command only when the sequence asks for it and the interlock permits it.",
         rung([("C", "#Open"), ("C", "#Permit")], [("COIL", "#Out")])),
        ("Valve is open", "Open limit switch made and closed limit switch not made.",
         rung([("C", "#ZSO"), ("CN", "#ZSC")], [("COIL", "#IsOpen")])),
        ("Valve is closed", "Closed limit switch made and open limit switch not made.",
         rung([("C", "#ZSC"), ("CN", "#ZSO")], [("COIL", "#IsClosed")])),
        ("Fault: did not open", "Open command on but the valve is not confirmed open within MonitorTime.",
         rung([("C", "#Out"), ("CN", "#IsOpen")], [("TON", "tOpen", "#MonitorTime", "#FaultNotOpen")])),
        ("Fault: did not close", "Open command off but the valve is not confirmed closed within MonitorTime.",
         rung([("CN", "#Out"), ("CN", "#IsClosed")], [("TON", "tClose", "#MonitorTime", "#FaultNotClosed")])),
    ]
    return lad_fb("FB_Valve", 101, "On/off valve with limit switch monitoring",
                  "LAD version. Same interface as scl/13_FB_Valve.scl.", iface, nets)


def fb_motor():
    iface = {
        "Input": [
            member("Run", "Bool", "Request: run the motor"),
            member("Permit", "Bool", "Interlock: motor may only run when TRUE", "true"),
            member("RunFb", "Bool", "Running feedback (contactor auxiliary contact)"),
            member("Trip", "Bool", "Motor protection breaker tripped"),
            member("FeedbackTime", "Time", "Max time from command to feedback", "T#5S"),
        ],
        "Output": [
            member("Out", "Bool", "Run command to the contactor"),
            member("Running", "Bool", "Motor confirmed running"),
            member("FaultTrip", "Bool", "Motor protection tripped"),
            member("FaultNoFeedback", "Bool", "Command on, but no running feedback within FeedbackTime"),
        ],
        "Static": [member("tFb", "TON_TIME", "Feedback monitoring time", version="1.0")],
    }
    nets = [
        ("Motor protection tripped", "The motor protection breaker reports a trip.",
         rung([("C", "#Trip")], [("COIL", "#FaultTrip")])),
        ("Run command", "Contactor on when requested, permitted by the interlock and not tripped.",
         rung([("C", "#Run"), ("C", "#Permit"), ("CN", "#Trip")], [("COIL", "#Out")])),
        ("Motor running", "Command on and running feedback from the contactor.",
         rung([("C", "#Out"), ("C", "#RunFb")], [("COIL", "#Running")])),
        ("Fault: no running feedback", "Command on but no feedback within FeedbackTime.",
         rung([("C", "#Out"), ("CN", "#RunFb")], [("TON", "tFb", "#FeedbackTime", "#FaultNoFeedback")])),
    ]
    return lad_fb("FB_Motor", 102, "Direct-on-line motor with running feedback and trip monitoring",
                  "LAD version. Same interface as scl/14_FB_Motor.scl.", iface, nets)


def fb_vfd():
    iface = {
        "Input": [
            member("Run", "Bool", "Request: run the drive"),
            member("Permit", "Bool", "Interlock: drive may only run when TRUE", "true"),
            member("SpeedSP", "Real", "Requested speed [rpm]"),
            member("SpeedPV", "Real", "Actual speed from the drive [rpm]"),
            member("Ready", "Bool", "Drive ready"),
            member("DriveFault", "Bool", "Drive reports a fault"),
            member("DeviationTime", "Time", "How long speed may be too low before a fault", "T#15S"),
        ],
        "Output": [
            member("RunOut", "Bool", "Run command to the drive"),
            member("SpeedOut", "Real", "Speed setpoint to the drive [rpm]"),
            member("AtSpeed", "Bool", "Actual speed within 10 % of setpoint"),
            member("FaultDrive", "Bool", "Drive fault"),
            member("FaultSpeedDev", "Bool", "Running, but speed more than 10 % below setpoint for DeviationTime"),
            member("NotReady", "Bool", "Drive not ready (and no fault): check enable / E-stop / main switch"),
        ],
        "Static": [member("tDev", "TON_TIME", "Speed deviation time", version="1.0")],
        "Temp": [member("SpeedMin", "Real", "90 % of the speed setpoint [rpm]")],
    }
    nets = [
        ("Drive fault", "The drive reports a fault.",
         rung([("C", "#DriveFault")], [("COIL", "#FaultDrive")])),
        ("Drive not ready", "Not ready and no fault: enable, E-stop or main switch is off.",
         rung([("CN", "#Ready"), ("CN", "#DriveFault")], [("COIL", "#NotReady")])),
        ("Run command", "Drive runs when requested, permitted by the interlock and ready.",
         rung([("C", "#Run"), ("C", "#Permit"), ("C", "#Ready")], [("COIL", "#RunOut")])),
        ("Speed setpoint when running", "Pass the requested speed to the drive while it runs.",
         rung([("C", "#RunOut")], [("MOVE", "#SpeedSP", "#SpeedOut")])),
        ("Speed setpoint when stopped", "Setpoint 0 rpm while the drive is not commanded.",
         rung([("CN", "#RunOut")], [("MOVE", "real:0.0", "#SpeedOut")])),
        ("Minimum speed", "90 % of the setpoint is the lowest speed that counts as at speed.",
         rung([], [("CALC", "Mul", "Real", "real:0.9", "#SpeedSP", "#SpeedMin")])),
        ("At speed", "Running and the actual speed is at least 90 % of the setpoint.",
         rung([("C", "#RunOut"), ("CMP", "Ge", "Real", "#SpeedPV", "#SpeedMin")], [("COIL", "#AtSpeed")])),
        ("Fault: speed deviation", "Running but not at speed for longer than DeviationTime.",
         rung([("C", "#RunOut"), ("CN", "#AtSpeed")], [("TON", "tDev", "#DeviationTime", "#FaultSpeedDev")])),
    ]
    return lad_fb("FB_VFD", 103, "Variable speed drive with speed monitoring",
                  "LAD version. Same interface as scl/15_FB_VFD.scl.", iface, nets)


# --------------------------------------------------------------------------------------------
# GRAPH batch sequence
# --------------------------------------------------------------------------------------------

GRAPH_STD_INPUTS = [
    ("OFF_SQ", "Bool"), ("INIT_SQ", "Bool"), ("ACK_EF", "Bool"), ("S_PREV", "Bool"), ("S_NEXT", "Bool"),
    ("SW_AUTO", "Bool"), ("SW_TAP", "Bool"), ("SW_TOP", "Bool"), ("SW_MAN", "Bool"), ("S_SEL", "Int"),
    ("S_ON", "Bool"), ("S_OFF", "Bool"), ("T_PUSH", "Bool"),
]
GRAPH_STD_OUTPUTS = [
    ("S_NO", "Int"), ("S_MORE", "Bool"), ("S_ACTIVE", "Bool"), ("ERR_FLT", "Bool"), ("AUTO_ON", "Bool"),
    ("TAP_ON", "Bool"), ("TOP_ON", "Bool"), ("MAN_ON", "Bool"),
]

# (GRAPH step number, step name, Step output value, max time, actions [(operand, interlocked)], interlocked step)
STEPS = [
    (1, "Idle", 0, "T#10S", [], False),
    (10, "FillA", 10, "T#120S", [("#OpenXV101", True)], True),
    (20, "FillB", 20, "T#120S", [("#OpenXV102", True)], True),
    (30, "MixHeat", 30, "T#600S", [("#RunM201", True), ("#HeatReq", True)], True),
    (40, "Hold", 40, "T#60S", [("#RunM201", True)], True),
    (50, "Drain", 50, "T#180S", [("#RunP401", True)], True),
    (60, "Done", 60, "T#10S", [("#BatchDone", False)], False),
]

# (transition number, name, from step, to step, series elements)
TRANSITIONS = [
    (1, "T1_StartBatch", 1, 10, [("CN", "#Halted"), ("C", "#Start"), ("C", "#RecipeValid"),
                              ("CMP", "Le", "Real", "#Level", '"DB_Recipe".DrainLevel')]),
    (2, "T2_LevelA_Reached", 10, 20, [("CN", "#Halted"), ("CMP", "Ge", "Real", "#Level", '"DB_Recipe".LevelA')]),
    (3, "T3_LevelB_Reached", 20, 30, [("CN", "#Halted"), ("CMP", "Ge", "Real", "#Level", '"DB_Recipe".LevelB')]),
    (4, "T4_HotAndMixed", 30, 40, [("CN", "#Halted"), ("CMP", "Ge", "Real", "#Temp", '"DB_Recipe".TempSP'),
                                ("CMP", "Ge", "Time", "#MixHeat.U", '"DB_Recipe".MixTime')]),
    (5, "T5_FillerReady", 40, 50, [("CN", "#Halted"), ("C", "#FillerReady")]),
    (6, "T6_TankEmpty", 50, 60, [("CN", "#Halted"), ("CMP", "Le", "Real", "#Level", '"DB_Recipe".DrainLevel')]),
    (7, "T7_BatchCounted", 60, 1, [("C", "#Done.X")]),
]

PRE_OPERATIONS = [
    # Recipe plausibility
    rung([("CMP", "Gt", "Real", '"DB_Recipe".LevelA', "real:0.0"),
          ("CMP", "Lt", "Real", '"DB_Recipe".LevelA', '"DB_Recipe".LevelB'),
          ("CMP", "Le", "Real", '"DB_Recipe".LevelB', "real:90.0"),
          ("CMP", "Ge", "Real", '"DB_Recipe".TempSP', "real:20.0"),
          ("CMP", "Le", "Real", '"DB_Recipe".TempSP', "real:85.0"),
          ("CMP", "Ge", "Real", '"DB_Recipe".MixSpeed', "real:100.0"),
          ("CMP", "Le", "Real", '"DB_Recipe".MixSpeed', "real:1500.0")],
         [("COIL", "#RecipeValid")]),
    # Start clears the pause; Stop, a fault or the E-stop set it (they win: they come last). So after a fault or
    # E-stop, Reset alone does not restart the batch: the operator has to press Start (EN 60204-1).
    rung([("C", "#Start")], [("R", "#Paused")]),
    rung([("C", "#Stop")], [("S", "#Paused")]),
    rung([("C", "#AnyFault")], [("S", "#Paused")]),
    rung([("CN", "#EStopOK")], [("S", "#Paused")]),
    # Healthy: no fault, E-stop OK, not paused, in Auto. Halted is the opposite.
    rung([("CN", "#AnyFault"), ("C", "#EStopOK"), ("CN", "#Paused"), ("C", "#Auto")], [("COIL", "#Healthy")]),
    rung([("CN", "#Healthy")], [("COIL", "#Halted")]),
]

POST_OPERATIONS = (
    # Step number and time in step (U = time without interlock error, so it pauses while halted)
    [rung([("C", f"#{name}.X")], [("MOVE", f"int:{value}", "#Step"), ("MOVE", f"#{name}.U", "#StepTime")])
     for _, name, value, _, _, _ in STEPS]
    + [
        rung([("CN", "#Idle.X")], [("COIL", "#Running")]),
        rung([("C", "#MixHeat.X")], [("MOVE", '"DB_Recipe".MixSpeed', "#SpeedM201")]),
        rung([("CN", "#MixHeat.X")], [("MOVE", '"DB_Recipe".HoldSpeed', "#SpeedM201")]),
        rung([("C", "#HeatReq"), ("CMP", "Lt", "Real", "#Temp", '"DB_Recipe".TempSP')], [("COIL", "#HeatE301")]),
        # Heating check: while the heater is off, remember the temperature and the step time. So the 60 s window
        # starts again each time the heater switches on, also after a halt (MixHeat.U keeps its value while halted).
        rung([("CN", "#HeatE301")], [("MOVE", "#Temp", "#TempAtStepStart"), ("MOVE", "#MixHeat.U", "#HeatOnSince")]),
        rung([], [("CALC", "Add", "Real", "#TempAtStepStart", "real:2.0", "#TempRiseLimit"),
                  ("CALC", "Sub", "Time", "#MixHeat.U", "#HeatOnSince", "#HeatOnTime")]),
        # Step supervision (the alarm handler latches these)
        rung([("C", "#FillA.X"), ("CMP", "Gt", "Time", "#FillA.U", "T#120S")], [("COIL", "#TimeoutStep10")]),
        rung([("C", "#FillB.X"), ("CMP", "Gt", "Time", "#FillB.U", "T#120S")], [("COIL", "#TimeoutStep20")]),
        rung([("C", "#MixHeat.X"), ("CMP", "Gt", "Time", "#MixHeat.U", "T#600S")], [("COIL", "#TimeoutStep30")]),
        rung([("C", "#Drain.X"), ("CMP", "Gt", "Time", "#Drain.U", "T#180S")], [("COIL", "#TimeoutStep50")]),
        rung([("C", "#Hold.X"), ("CMP", "Gt", "Time", "#Hold.U", "T#60S"), ("CN", "#FillerReady")],
             [("COIL", "#WaitFillerTooLong")]),
        rung([("C", "#MixHeat.X"), ("C", "#HeatE301"), ("CMP", "Gt", "Time", "#HeatOnTime", "T#60S"),
              ("CMP", "Lt", "Real", "#Temp", "#TempRiseLimit")], [("COIL", "#NoHeating")]),
        # Start checks (only in Idle)
        rung([("C", "#Idle.X"), ("C", "#Start"), ("CN", "#Auto")], [("COIL", "#StartNotAuto")]),
        rung([("C", "#Idle.X"), ("C", "#Start"), ("CMP", "Gt", "Real", "#Level", '"DB_Recipe".DrainLevel')],
             [("COIL", "#StartTankNotEmpty")]),
        rung([("C", "#Idle.X"), ("C", "#Start"), ("CN", "#RecipeValid")], [("COIL", "#StartRecipeInvalid")]),
    ]
)


IL_ATTR = ' Interlock="true"'


def graph_fb(version):
    vsuffix = "V" + version.split(".")[0]
    user_inputs = [
        member("Start", "Bool", "Start button or MES start request"),
        member("Stop", "Bool", "Stop button: pause the batch"),
        member("Auto", "Bool", "Selector in Auto"),
        member("AnyFault", "Bool", "Any FAULT alarm active"),
        member("EStopOK", "Bool", "Emergency stop circuit healthy"),
        member("Level", "Real", "Tank level [%]"),
        member("Temp", "Real", "Tank temperature [degC]"),
        member("FillerReady", "Bool", "Downstream filler ready to receive"),
    ]
    user_outputs = [
        member("Step", "Int", "Current step: 0 Idle, 10 Fill A, 20 Fill B, 30 Mix and heat, 40 Hold, 50 Drain, 60 Done"),
        member("StepTime", "Time", "Time in the current step without interlock error (pauses while halted)"),
        member("Halted", "Bool", "Sequence paused: outputs off (fault, E-stop, Stop or not in Auto)"),
        member("Running", "Bool", "A batch is active (step 10..60)"),
        member("OpenXV101", "Bool", "Command: open inlet valve A"),
        member("OpenXV102", "Bool", "Command: open inlet valve B"),
        member("RunM201", "Bool", "Command: run agitator"),
        member("SpeedM201", "Real", "Command: agitator speed [rpm]"),
        member("HeatE301", "Bool", "Command: heater on"),
        member("RunP401", "Bool", "Command: run outlet pump"),
        member("BatchDone", "Bool", "Pulse when a batch finishes"),
        member("RecipeValid", "Bool", "Recipe values are plausible"),
        member("TimeoutStep10", "Bool", "Step 10 longer than 120 s"),
        member("TimeoutStep20", "Bool", "Step 20 longer than 120 s"),
        member("TimeoutStep30", "Bool", "Step 30 longer than 600 s"),
        member("TimeoutStep50", "Bool", "Step 50 longer than 180 s"),
        member("WaitFillerTooLong", "Bool", "Step 40 waiting for filler longer than 60 s"),
        member("NoHeating", "Bool", "Heater on for 60 s in step 30 but temperature rose less than 2 degC"),
        member("StartNotAuto", "Bool", "Start pressed while not in Auto"),
        member("StartTankNotEmpty", "Bool", "Start pressed while tank not empty"),
        member("StartRecipeInvalid", "Bool", "Start pressed while recipe invalid"),
    ]
    user_statics = [
        member("Paused", "Bool", "Set by Stop, a fault or the E-stop; cleared by Start"),
        member("Healthy", "Bool", "No fault, E-stop OK, not paused and in Auto: the step interlocks are fulfilled"),
        member("HeatReq", "Bool", "Step 30 asks for heat (heater runs while below TempSP)"),
        member("TempAtStepStart", "Real", "Temperature when the heater last switched on [degC]"),
        member("TempRiseLimit", "Real", "TempAtStepStart + 2 degC"),
        member("HeatOnSince", "Time", "Step 30 time when the heater last switched on"),
        member("HeatOnTime", "Time", "How long the heater has been on without a break"),
    ]
    graph_statics = [member("RT_DATA", f"G7_RTDataPlus_{vsuffix}", version="1.0")]
    graph_statics += [member(name, f"G7_TransitionPlus_{vsuffix}", version="1.0") for _, name, _, _, _ in TRANSITIONS]
    graph_statics += [member(name, f"G7_StepPlus_{vsuffix}", version="1.0") for _, name, _, _, _, _ in STEPS]

    base = ('<Section Name="Base"><Sections Datatype="GRAPH_BASE" Version="1.0"><Section Name="Input" />'
            '<Section Name="Output" /><Section Name="InOut" /><Section Name="Static" /></Sections></Section>')
    sections = (base
                + section("Input", [member(n, t) for n, t in GRAPH_STD_INPUTS] + user_inputs)
                + section("Output", [member(n, t) for n, t in GRAPH_STD_OUTPUTS] + user_outputs)
                + section("InOut", [])
                + section("Static", user_statics + graph_statics)
                + section("Temp", []) + section("Constant", []))

    def perm(nets):
        if not nets:
            return ""
        return "".join(f'<PermanentOperation ProgrammingLanguage="LAD">{n.render(xmlns=False)}</PermanentOperation>'
                       for n in nets)

    steps_xml = []
    for number, name, _, tmax, actions, interlocked in STEPS:
        acts = "".join(
            f'<Action{IL_ATTR if il else ""} Qualifier="N"><Token Text="{attr(op)}" />'
            f'<Token Text="&#xA;" /></Action>' for op, il in actions) + "<Action />"
        sv = rung([], [("SVCOIL",)]).render(xmlns=False)
        if interlocked:
            il = rung([("C", "#Healthy")], [("ILCOIL",)]).render(xmlns=False)
            il_text = (f'<AlarmText><MultiLanguageText Lang="{CULTURE}">Sequence halted: fault, E-stop, Stop or '
                       f"not in Auto</MultiLanguageText></AlarmText>")
        else:
            il = rung([], [("ILCOIL",)]).render(xmlns=False)
            il_text = ""
        steps_xml.append(
            f'<Step Number="{number}" Init="{"true" if number == 1 else "false"}" Name="{name}" '
            f'MaximumStepTime="{tmax}" WarningTime="{tmax}">'
            f"<Actions>{acts}</Actions>"
            f'<Supervisions><Supervision ProgrammingLanguage="LAD">{sv}</Supervision></Supervisions>'
            f'<Interlocks><Interlock ProgrammingLanguage="LAD">{il_text}{il}</Interlock></Interlocks></Step>')

    trans_xml, conns = [], []
    for number, name, src, dst, series in TRANSITIONS:
        trans_xml.append(f'<Transition IsMissing="false" Name="{name}" Number="{number}" ProgrammingLanguage="LAD">'
                         f"{rung(series, [('TRCOIL',)]).render(xmlns=False)}</Transition>")
        conns.append(f'<Connection><NodeFrom><StepRef Number="{src}" /></NodeFrom><NodeTo><TransitionRef Number="{number}" />'
                     f"</NodeTo><LinkType>Direct</LinkType></Connection>")
        link = "Jump" if dst == 1 else "Direct"
        conns.append(f'<Connection><NodeFrom><TransitionRef Number="{number}" /></NodeFrom><NodeTo><StepRef Number="{dst}" />'
                     f"</NodeTo><LinkType>{link}</LinkType></Connection>")

    seq_comment = ("Batch sequence of the mixing station. Idle waits for Start; Fill A and Fill B open the inlet "
                   "valves until the recipe levels; Mix and heat runs the agitator and heats to TempSP for MixTime; "
                   "Hold waits for the filler; Drain pumps the tank empty; Done counts the batch. Every working "
                   "step is interlocked with Healthy: on a fault, E-stop, Stop or Auto off its actions switch off "
                   "and the step is kept. After a fault or E-stop, press Reset, then Start to continue; Reset alone "
                   "does not restart the batch.")
    alarms = ('<AlarmsSettings><AlarmSupervisionCategories>'
              + "".join(f'<AlarmSupervisionCategory Id="{i}" DisplayClass="0" />' for i in range(1, 9))
              + '</AlarmSupervisionCategories><AlarmInterlockCategory Id="1" /><AlarmSubcategory1Interlock Id="0" />'
              '<AlarmSubcategory2Interlock Id="0" /><AlarmCategorySupervision Id="1" />'
              '<AlarmSubcategory1Supervision Id="0" /><AlarmSubcategory2Supervision Id="0" />'
              '<AlarmWarningCategory Id="2" /><AlarmSubcategory1Warning Id="0" /><AlarmSubcategory2Warning Id="0" />'
              "</AlarmsSettings>")
    graph = (f'<Graph xmlns="{NS_GRAPH}">'
             f"<PreOperations>{perm(PRE_OPERATIONS)}</PreOperations>"
             f'<Sequence><Title /><Comment><MultiLanguageText Lang="{CULTURE}">{escape(seq_comment)}</MultiLanguageText></Comment>'
             f"<Steps>{''.join(steps_xml)}</Steps><Transitions>{''.join(trans_xml)}</Transitions>"
             f"<Branches /><Connections>{''.join(conns)}</Connections></Sequence>"
             f"<PostOperations>{perm(POST_OPERATIONS)}</PostOperations>{alarms}</Graph>")

    ids = Ids()
    block_comment = mtext(ids, "Comment", "GRAPH version. Same inputs and outputs as scl/16_FB_BatchSequence.scl.")
    cu_id = ids.next()
    cu_c = mtext(ids, "Comment", "")
    cu_t = mtext(ids, "Title", "")
    block_title = mtext(ids, "Title", "Batch sequence: fill A, fill B, mix and heat, hold, drain")
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n<Document><Engineering version="V20" />'
        '<SW.Blocks.FB ID="0"><AttributeList>'
        f"<GraphVersion>{version}</GraphVersion>"
        "<HeaderAuthor>Demo</HeaderAuthor><HeaderFamily /><HeaderName /><HeaderVersion>0.2</HeaderVersion>"
        f'<Interface><Sections xmlns="{NS_IF}">{sections}</Sections></Interface>'
        "<Name>FB_BatchSequence</Name><Namespace /><Number>104</Number>"
        "<ProgrammingLanguage>GRAPH</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>"
        f"</AttributeList><ObjectList>{block_comment}"
        f'<SW.Blocks.CompileUnit ID="{cu_id}" CompositionName="CompileUnits"><AttributeList>'
        f"<NetworkSource>{graph}</NetworkSource><ProgrammingLanguage>GRAPH</ProgrammingLanguage></AttributeList>"
        f"<ObjectList>{cu_c}{cu_t}</ObjectList></SW.Blocks.CompileUnit>{block_title}</ObjectList>"
        "</SW.Blocks.FB></Document>\n")


# --------------------------------------------------------------------------------------------

def pretty(text):
    import xml.dom.minidom
    # Keep the GRAPH action line breaks as character references (a raw newline in an attribute becomes a space).
    dom = xml.dom.minidom.parseString(text.replace("&#xA;", "@@NL@@").encode("utf-8"))
    out = dom.toprettyxml(indent="  ", encoding="utf-8").decode("utf-8").replace("@@NL@@", "&#xA;")
    return "\n".join(line for line in out.splitlines() if line.strip()) + "\n"


def main():
    global CULTURE
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    ap.add_argument("--graph-version", default="2.0", help="GRAPH block version, e.g. 2.0 (default) or 6.0")
    ap.add_argument("--culture", default="en-US", help="Project language used for comments and titles")
    a = ap.parse_args()
    CULTURE = a.culture

    files = {
        os.path.join("lad", "13_FB_Valve.xml"): fb_valve(),
        os.path.join("lad", "14_FB_Motor.xml"): fb_motor(),
        os.path.join("lad", "15_FB_VFD.xml"): fb_vfd(),
        os.path.join("graph", "16_FB_BatchSequence.xml"): graph_fb(a.graph_version),
    }
    for rel, xml in files.items():
        path = os.path.join(a.out, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\r\n") as f:
            f.write(pretty(xml))
        print("wrote", os.path.normpath(path))


if __name__ == "__main__":
    main()
