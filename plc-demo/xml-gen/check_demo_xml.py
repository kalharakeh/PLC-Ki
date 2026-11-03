#!/usr/bin/env python3
"""Offline checks for the generated LAD/GRAPH XML (no TIA needed).

    python check_demo_xml.py

Checks per network: UIds unique, every wire end points to a part or operand that exists, every
part input is wired. Checks per block: every #local operand exists in the block interface (or is a
GRAPH step/transition), every "DB".member exists in the SCL data block sources, and the interface
matches the SCL version of the same block.
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
DEMO = os.path.normpath(os.path.join(HERE, ".."))
IF_NS = "{http://www.siemens.com/automation/Openness/SW/Interface/v5}"

REQUIRED_INPUTS = {
    "Contact": ["in", "operand"], "Coil": ["in", "operand"], "SCoil": ["in", "operand"], "RCoil": ["in", "operand"],
    "TrCoil": ["in"], "IlCoil": ["in"], "SvCoil": ["in"], "Move": ["en", "in", "out1"],
    "Add": ["en", "in1", "in2", "out"], "Mul": ["en", "in1", "in2", "out"], "Sub": ["en", "in1", "in2", "out"],
    "Gt": ["pre", "in1", "in2"], "Ge": ["pre", "in1", "in2"], "Lt": ["pre", "in1", "in2"],
    "Le": ["pre", "in1", "in2"], "Eq": ["pre", "in1", "in2"], "TON": ["IN", "PT"],
}


def local(tag):
    return tag.split("}")[-1]


def scl_db_members():
    members = {}
    for path in glob.glob(os.path.join(DEMO, "scl", "*.db")):
        text = open(path, encoding="utf-8").read()
        m = re.search(r'DATA_BLOCK\s+"([^"]+)"', text)
        if not m:
            continue
        members[m.group(1)] = set(re.findall(r"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\{[^}]*\})?\s*:", text, re.M))
    return members


def scl_interface(name):
    for path in glob.glob(os.path.join(DEMO, "scl", "*.scl")):
        text = open(path, encoding="utf-8").read()
        if f'FUNCTION_BLOCK "{name}"' not in text:
            continue
        out = {}
        for sec, body in re.findall(r"VAR(_INPUT|_OUTPUT|_IN_OUT)?\s*\n(.*?)END_VAR", text, re.S):
            key = {"_INPUT": "Input", "_OUTPUT": "Output", "_IN_OUT": "InOut", "": "Static"}[sec]
            for mname, mtype in re.findall(r"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\{[^}]*\})?\s*:\s*([^;:=]+?)\s*(?::=[^;]*)?;", body, re.M):
                out.setdefault(key, {})[mname] = mtype.strip().strip('"')
        return out
    return None


def check_flgnet(flg, where, errors):
    uids, kinds = {}, {}
    for el in flg.iter():
        u = el.get("UId")
        if u is None or local(el.tag) in ("NameCon", "IdentCon"):
            continue
        if u in uids:
            errors.append(f"{where}: duplicate UId {u}")
        uids[u] = el
    parts = {p.get("UId"): p for p in flg.iter() if local(p.tag) == "Part"}
    connected = {}
    for wire in [w for w in flg.iter() if local(w.tag) == "Wire"]:
        for c in wire:
            t = local(c.tag)
            if t in ("NameCon", "IdentCon"):
                u = c.get("UId")
                if u not in uids:
                    errors.append(f"{where}: wire {wire.get('UId')} points to missing UId {u}")
                if t == "NameCon":
                    connected.setdefault(u, set()).add(c.get("Name"))
    for u, p in parts.items():
        need = REQUIRED_INPUTS.get(p.get("Name"))
        if need is None:
            errors.append(f"{where}: unknown part {p.get('Name')}")
            continue
        missing = [n for n in need if n not in connected.get(u, set())]
        if missing:
            errors.append(f"{where}: part {p.get('Name')} UId {u} has unwired {missing}")
    return [a for a in flg.iter() if local(a.tag) == "Access"]


def main():
    db_members = scl_db_members()
    errors = []
    files = sorted(glob.glob(os.path.join(DEMO, "lad", "*.xml")) + glob.glob(os.path.join(DEMO, "graph", "*.xml")))
    for path in files:
        name = os.path.relpath(path, DEMO)
        root = ET.parse(path).getroot()
        fb = root.find("SW.Blocks.FB")
        block = fb.find("AttributeList/Name").text
        iface = {}
        for sec in fb.iter(IF_NS + "Section"):
            if sec.get("Name") in ("Input", "Output", "InOut", "Static", "Temp") and sec.find(IF_NS + "Sections") is None:
                for m in sec.findall(IF_NS + "Member"):
                    iface.setdefault(sec.get("Name"), {})[m.get("Name")] = m.get("Datatype")
        all_names = {n for s in iface.values() for n in s}
        dupes = sum(len(s) for s in iface.values()) - len(all_names)
        if dupes:
            errors.append(f"{name}: {dupes} duplicate interface member name(s)")

        accesses = []
        for i, flg in enumerate(el for el in root.iter() if local(el.tag) == "FlgNet"):
            accesses += check_flgnet(flg, f"{name} network {i + 1}", errors)
        for a in accesses:
            comps = [c.get("Name") for c in a.iter() if local(c.tag) == "Component"]
            if a.get("Scope") == "LocalVariable" and comps[0] not in all_names:
                errors.append(f"{name}: #{'.'.join(comps)} not in interface")
            if a.get("Scope") == "GlobalVariable":
                if comps[0] not in db_members or comps[1] not in db_members[comps[0]]:
                    errors.append(f'{name}: "{comps[0]}".{".".join(comps[1:])} not found in scl/*.db')
        for tok in (t for t in root.iter() if local(t.tag) == "Token"):
            text = tok.get("Text")
            if text.startswith("#") and text[1:] not in all_names:
                errors.append(f"{name}: action operand {text} not in interface")

        # Same interface as the SCL block it replaces (GRAPH adds its own system members).
        scl = scl_interface(block)
        if scl:
            for sec in ("Input", "Output"):
                for mname, mtype in scl.get(sec, {}).items():
                    got = iface.get(sec, {}).get(mname)
                    if got is None:
                        errors.append(f"{name}: {sec} {mname} missing (SCL has it)")
                    elif got.lower() != mtype.lower():
                        errors.append(f"{name}: {sec} {mname} is {got}, SCL has {mtype}")
        print(f"checked {name}: {len(accesses)} operands")
    for e in errors:
        print("ERROR", e)
    print("OK" if not errors else f"{len(errors)} error(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
