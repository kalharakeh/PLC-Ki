using System.Text;
using System.Xml.Linq;

namespace PlcKi.Core.Import;

/// <summary>
/// Turns LAD/FBD networks (SimaticML FlgNet) into short SCL-like statements, so Claude can read the logic of
/// LAD, FBD and GRAPH blocks the same way it reads SCL:
///   #IsOpen := #ZSO AND NOT #ZSC;
///   #tOpen(IN := #Out AND NOT #IsOpen, PT := #MonitorTime);
///   #FaultNotOpen := #tOpen.Q;
/// Works on the wire graph: every input pin is the expression of what is wired to it, every coil, box output
/// and assignment becomes one statement. Parts it does not know are shown as a call with their pins.
/// </summary>
public static class NetworkDecoder
{
    /// <summary>The statements of one network, in the order of its parts.</summary>
    public static List<string> Decode(XElement flgNet) => new Net(flgNet).Statements();

    /// <summary>The condition wired into the network's coil of the given kind (TrCoil, IlCoil, SvCoil), or null.</summary>
    public static string? CoilCondition(XElement flgNet, string coil) => new Net(flgNet).CoilInput(coil);

    private sealed class PartInfo
    {
        public string Uid = "";
        public string Name = "";             // Contact, Coil, TON, Add, ... or "Call" for block calls
        public string? Operand;              // for Access parts: the variable or constant text
        public string? Instance;             // timers, counters, FB calls
        public HashSet<string> Negated = new();
        public bool IsAccess => Operand != null;
    }

    private sealed class Net
    {
        private readonly Dictionary<string, PartInfo> _parts = new();
        private readonly List<PartInfo> _order = new();
        // (part uid, pin) -> sources wired into it; a source is "rail", "uid" (access) or "uid:pin" (part output)
        private readonly Dictionary<(string, string), List<string>> _inputs = new();
        // part output (uid:pin) -> variables it is written to
        private readonly Dictionary<string, List<string>> _assigned = new();
        private readonly Dictionary<string, string> _memo = new();
        private readonly HashSet<string> _busy = new();

        public Net(XElement flg)
        {
            var parts = Child(flg, "Parts");
            if (parts != null)
                foreach (var p in parts.Elements())
                {
                    var info = ReadPart(p);
                    if (info == null) continue;
                    _parts[info.Uid] = info;
                    if (!info.IsAccess) _order.Add(info);
                }
            var wires = Child(flg, "Wires");
            if (wires == null) return;
            foreach (var w in wires.Elements().Where(e => e.Name.LocalName == "Wire"))
            {
                var ends = w.Elements().ToList();
                if (ends.Count < 2) continue;
                var src = Source(ends[0]);
                foreach (var t in ends.Skip(1))
                {
                    var uid = t.Attribute("UId")?.Value ?? "";
                    switch (t.Name.LocalName)
                    {
                        case "NameCon":
                            Add(_inputs, (uid, t.Attribute("Name")?.Value ?? ""), src);
                            break;
                        case "IdentCon" when src != null && src.Contains(':'):
                            // a box output written to a variable
                            if (_parts.TryGetValue(uid, out var v) && v.Operand != null)
                                Add(_assigned, src, v.Operand);
                            break;
                    }
                }
            }
        }

        private static string? Source(XElement e) => e.Name.LocalName switch
        {
            "Powerrail" => "rail",
            "IdentCon" => e.Attribute("UId")?.Value,
            "NameCon" => e.Attribute("UId")?.Value + ":" + e.Attribute("Name")?.Value,
            _ => null,   // OpenCon: output not used
        };

        private static void Add<TK>(Dictionary<TK, List<string>> d, TK key, string? value) where TK : notnull
        {
            if (value == null) return;
            if (!d.TryGetValue(key, out var l)) d[key] = l = new List<string>();
            l.Add(value);
        }

        private static PartInfo? ReadPart(XElement p)
        {
            var uid = p.Attribute("UId")?.Value;
            if (uid == null) return null;
            switch (p.Name.LocalName)
            {
                case "Access":
                    return new PartInfo { Uid = uid, Name = "Access", Operand = AccessText(p) };
                case "Part":
                {
                    var info = new PartInfo { Uid = uid, Name = p.Attribute("Name")?.Value ?? "?" };
                    foreach (var n in p.Elements().Where(e => e.Name.LocalName == "Negated"))
                        info.Negated.Add(n.Attribute("Name")?.Value ?? "");
                    var inst = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
                    if (inst != null) info.Instance = InstanceText(inst);
                    return info;
                }
                case "Call":
                {
                    var ci = p.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
                    var inst = ci?.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
                    var info = new PartInfo { Uid = uid, Name = "\"" + (ci?.Attribute("Name")?.Value ?? "?") + "\"" };
                    if (inst != null) info.Instance = InstanceText(inst);
                    return info;
                }
                default:
                    return null;
            }
        }

        /// <summary>The coil's input condition, for GRAPH transitions, interlocks and supervisions.</summary>
        public string? CoilInput(string coil)
        {
            var c = _order.FirstOrDefault(p => p.Name == coil);
            return c == null ? null : In(c, "in") ?? "FALSE";
        }

        public List<string> Statements()
        {
            var list = new List<string>();
            foreach (var p in _order)
            {
                var en = In(p, "in");
                switch (p.Name)
                {
                    case "Contact" or "PContact" or "NContact" or "Ge" or "Gt" or "Le" or "Lt" or "Eq" or "Ne" or "O" or "A" or "X" or "Not":
                        break;   // logic only, shows up where it is used
                    case "Coil":
                        list.Add($"{In(p, "operand") ?? "?"} := {(p.Negated.Contains("operand") ? Not(en ?? "FALSE") : en ?? "FALSE")};");
                        break;
                    case "SCoil":
                        list.Add($"IF {en ?? "FALSE"} THEN {In(p, "operand") ?? "?"} := TRUE; END_IF;   // set");
                        break;
                    case "RCoil":
                        list.Add($"IF {en ?? "FALSE"} THEN {In(p, "operand") ?? "?"} := FALSE; END_IF;   // reset");
                        break;
                    case "TrCoil":
                        list.Add($"transition condition := {en ?? "FALSE"};");
                        break;
                    case "IlCoil":
                        list.Add($"interlock condition := {en ?? "FALSE"};");
                        break;
                    case "SvCoil":
                        list.Add($"supervision condition := {en ?? "FALSE"};");
                        break;
                    default:
                        list.AddRange(Box(p));
                        break;
                }
            }
            return list;
        }

        /// <summary>A box (timer, math, move, block call): one call line plus one line per output written to a variable.</summary>
        private IEnumerable<string> Box(PartInfo p)
        {
            var pins = _inputs.Keys.Where(k => k.Item1 == p.Uid).Select(k => k.Item2).ToList();
            var en = pins.Contains("en") ? In(p, "en") : null;
            var args = pins.Where(x => x is not "en")
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(x => $"{x} := {In(p, x)}").ToList();
            var outs = _assigned.Where(a => a.Key.StartsWith(p.Uid + ":")).ToList();
            string Guard(string s) => en == null || en == "TRUE" ? s : $"IF {en} THEN {s} END_IF;";

            var math = p.Name switch { "Add" => " + ", "Sub" => " - ", "Mul" => " * ", "Div" => " / ", "Mod" => " MOD ", _ => null };
            if (math != null || p.Name == "Move")
            {
                var value = math != null
                    ? string.Join(math, pins.Where(x => x.StartsWith("in")).OrderBy(x => x, StringComparer.Ordinal).Select(x => In(p, x)))
                    : In(p, "in") ?? "?";
                foreach (var (_, vars) in outs)
                    foreach (var v in vars) yield return Guard($"{v} := {value};");
                yield break;
            }

            var who = p.Instance ?? p.Name;
            var head = p.Instance != null && !p.Name.StartsWith('"') ? $"{who}({string.Join(", ", args)});   // {p.Name}" : $"{who}({string.Join(", ", args)});";
            yield return Guard(head);
            foreach (var (src, vars) in outs)
                foreach (var v in vars) yield return $"{v} := {who}.{src[(src.IndexOf(':') + 1)..]};";
        }

        /// <summary>The expression wired into a pin, NOT applied for a negated pin, or null when nothing is wired.</summary>
        private string? In(PartInfo p, string pin)
        {
            if (!_inputs.TryGetValue((p.Uid, pin), out var srcs) || srcs.Count == 0) return null;
            var parts = srcs.Select(Value).ToList();
            var e = parts.Count == 1 ? parts[0] : Or(parts);
            return p.Negated.Contains(pin) && pin != "operand" ? Not(e) : e;
        }

        private string Value(string src)
        {
            if (src == "rail") return "TRUE";
            var i = src.IndexOf(':');
            if (i < 0) return _parts.TryGetValue(src, out var a) ? a.Operand ?? "?" : "?";
            if (_memo.TryGetValue(src, out var m)) return m;
            if (!_busy.Add(src)) return "?";   // loop guard
            var v = Output(_parts.GetValueOrDefault(src[..i]), src[(i + 1)..]);
            _busy.Remove(src);
            return _memo[src] = v;
        }

        /// <summary>What a part puts out on a pin, as an expression.</summary>
        private string Output(PartInfo? p, string pin)
        {
            if (p == null) return "?";
            var pre = p.Name switch { "Contact" or "PContact" or "NContact" or "Coil" or "SCoil" or "RCoil" => In(p, "in"), _ => In(p, "pre") };
            string Operand() => In(p, "operand") ?? "?";
            string Cmp(string op) => And(pre, $"{In(p, "in1")} {op} {In(p, "in2")}");
            var value = p.Name switch
            {
                "Contact" => And(pre, p.Negated.Contains("operand") ? Not(Operand()) : Operand()),
                "PContact" => And(pre, $"rising edge of {Operand()}"),
                "NContact" => And(pre, $"falling edge of {Operand()}"),
                "Coil" or "SCoil" or "RCoil" => pre ?? "FALSE",
                "Ge" => Cmp(">="), "Gt" => Cmp(">"), "Le" => Cmp("<="), "Lt" => Cmp("<"), "Eq" => Cmp("="), "Ne" => Cmp("<>"),
                "O" => Or(Inputs(p)),
                "A" => AndAll(Inputs(p)),
                "X" => string.Join(" XOR ", Inputs(p).Select(Paren)),
                "Not" => Not(In(p, "in") ?? "?"),
                _ when pin is "eno" => "ENO",
                _ => $"{p.Instance ?? p.Name}.{pin}",
            };
            return value;
        }

        private List<string> Inputs(PartInfo p) => _inputs.Keys.Where(k => k.Item1 == p.Uid && k.Item2.StartsWith("in"))
            .OrderBy(k => k.Item2, StringComparer.Ordinal).Select(k => In(p, k.Item2) ?? "?").ToList();
    }

    private static string And(string? a, string b) => a == null || a == "TRUE" ? b : $"{Paren(a)} AND {Paren(b)}";
    private static string AndAll(List<string> xs) => xs.Count == 0 ? "TRUE" : xs.Skip(1).Aggregate(xs[0], (a, b) => And(a, b));
    private static string Or(List<string> xs) => xs.Contains("TRUE") ? "TRUE" : "(" + string.Join(" OR ", xs) + ")";
    private static string Not(string e) => e.Contains(' ') && !(e.StartsWith('(') && e.EndsWith(')')) ? $"NOT ({e})" : $"NOT {e}";
    /// <summary>Brackets an OR term that sits inside an AND; ANDs and single operands stay as they are.</summary>
    private static string Paren(string e) => e.Contains(" OR ") && !(e.StartsWith('(') && e.EndsWith(')')) ? $"({e})" : e;

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    /// <summary>"#Level", "\"DB_Recipe\".LevelA", "T#10S", "0.9".</summary>
    public static string AccessText(XElement access)
    {
        var scope = access.Attribute("Scope")?.Value ?? "";
        var constant = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Constant");
        if (constant != null)
        {
            var value = constant.Elements().FirstOrDefault(e => e.Name.LocalName == "ConstantValue")?.Value ?? "?";
            return value;
        }
        var symbol = access.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
        if (symbol == null) return "?";
        var comps = symbol.Elements().Where(e => e.Name.LocalName == "Component")
            .Select((c, i) => ComponentText(c, !(i == 0 && scope == "GlobalVariable"))).ToList();
        if (comps.Count == 0) return "?";
        return scope == "GlobalVariable"
            ? $"\"{comps[0]}\"" + string.Concat(comps.Skip(1).Select(c => "." + c))
            : "#" + string.Join(".", comps);
    }

    private static string ComponentText(XElement c) => ComponentText(c, true);

    private static string ComponentText(XElement c, bool quote)
    {
        // Names like "Read list_1" need quotes, as in TIA ("DB"."Read list_1".Variable)
        var name = c.Attribute("Name")?.Value ?? "?";
        if (quote) name = ProgramExport.Quote(name);
        var idx = c.Elements().Where(e => e.Name.LocalName == "Access").Select(AccessText).ToList();
        return idx.Count > 0 ? $"{name}[{string.Join(", ", idx)}]" : name;
    }

    private static string InstanceText(XElement inst)
    {
        var global = inst.Attribute("Scope")?.Value == "GlobalVariable";
        var comps = inst.Descendants().Where(e => e.Name.LocalName == "Component")
            .Select((c, i) => ComponentText(c, !(i == 0 && global))).ToList();
        if (comps.Count == 0) return "?";
        return global
            ? $"\"{comps[0]}\"" + string.Concat(comps.Skip(1).Select(c => "." + c))
            : "#" + string.Join(".", comps);
    }

    /// <summary>
    /// An SCL network inside a LAD/FBD block (TIA V17+) as plain SCL text, rebuilt from its StructuredText tokens.
    /// Pure SCL blocks are covered by the generated SCL source instead.
    /// </summary>
    public static string DecodeScl(XElement structuredText)
    {
        var sb = new StringBuilder();
        foreach (var e in structuredText.Elements()) Scl(e, sb);
        // Drop trailing blanks on each line and empty lines at the ends
        var lines = sb.ToString().Replace("\r", "").Split('\n').Select(l => l.TrimEnd());
        return string.Join("\n", lines).Trim('\n');
    }

    private static void Scl(XElement e, StringBuilder sb)
    {
        int Num() => int.TryParse(e.Attribute("Num")?.Value, out var n) ? n : 1;
        switch (e.Name.LocalName)
        {
            case "Token": sb.Append(e.Attribute("Text")?.Value); break;
            case "Blank": sb.Append(' ', Num()); break;
            case "NewLine": sb.Append('\n', Num()); break;
            case "LineComment": sb.Append("//").Append(string.Concat(e.Elements().Where(x => x.Name.LocalName == "Text").Select(x => x.Value))); break;
            case "Comment": sb.Append("(*").Append(string.Concat(e.Elements().Where(x => x.Name.LocalName == "Text").Select(x => x.Value))).Append("*)"); break;
            case "Text": sb.Append(e.Value); break;
            case "Access": SclAccess(e, sb); break;
            case "Parameter":
                sb.Append(e.Attribute("Name")?.Value);
                foreach (var c in e.Elements()) Scl(c, sb);
                break;
            case "Instruction":
                sb.Append(e.Attribute("Name")?.Value);
                foreach (var c in e.Elements()) Scl(c, sb);
                break;
            case "CallInfo":
            {
                var inst = e.Elements().FirstOrDefault(x => x.Name.LocalName == "Instance");
                if (inst != null) sb.Append(InstanceText(inst));
                else if (e.Attribute("Name")?.Value is { } n) sb.Append('"').Append(n).Append('"');
                foreach (var c in e.Elements().Where(x => x.Name.LocalName != "Instance")) Scl(c, sb);
                break;
            }
            case "DataType": sb.Append(e.Value); break;
            case "Constant" or "PredefinedVariable" when e.Attribute("Name") != null: sb.Append(e.Attribute("Name")!.Value); break;
            case "AttributeList": break;
            default:
                foreach (var c in e.Elements()) Scl(c, sb);
                break;
        }
    }

    private static void SclAccess(XElement access, StringBuilder sb)
    {
        var scope = access.Attribute("Scope")?.Value ?? "";
        var symbol = access.Elements().FirstOrDefault(x => x.Name.LocalName == "Symbol");
        switch (scope)
        {
            case "LocalVariable" or "GlobalVariable" when symbol != null:
                sb.Append(scope == "LocalVariable" ? "#" : "");
                var first = true;
                foreach (var c in symbol.Elements())
                {
                    if (c.Name.LocalName == "Component")
                    {
                        var name = c.Attribute("Name")?.Value ?? "?";
                        sb.Append(first && scope == "GlobalVariable" ? $"\"{name}\"" : ProgramExport.Quote(name));
                        foreach (var ix in c.Elements()) Scl(ix, sb);   // array index: [ ... ]
                        first = false;
                    }
                    else Scl(c, sb);
                }
                break;
            case "LiteralConstant" or "TypedConstant":
                sb.Append(access.Descendants().FirstOrDefault(x => x.Name.LocalName == "ConstantValue")?.Value ?? "?");
                break;
            case "LocalConstant":
                sb.Append('#').Append(access.Descendants().FirstOrDefault(x => x.Name.LocalName == "Constant")?.Attribute("Name")?.Value);
                break;
            case "GlobalConstant":
                sb.Append('"').Append(access.Descendants().FirstOrDefault(x => x.Name.LocalName == "Constant")?.Attribute("Name")?.Value).Append('"');
                break;
            default:
                foreach (var c in access.Elements()) Scl(c, sb);
                break;
        }
    }

    /// <summary>
    /// A GRAPH block as text: the sequence comment, every step with its time limits, actions, interlock and
    /// supervision, every transition with its condition and where it leads, and the permanent pre/post operations.
    /// </summary>
    public static string DecodeGraph(XElement graph)
    {
        var sb = new StringBuilder();
        void Ops(string kind, string title)
        {
            var ops = graph.Descendants().Where(e => e.Name.LocalName == kind).SelectMany(e => e.Elements())
                .Where(e => e.Name.LocalName == "PermanentOperation").ToList();
            if (ops.Count == 0) return;
            sb.AppendLine(title);
            foreach (var op in ops)
            {
                var flg = op.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
                if (flg == null) continue;
                foreach (var s in Decode(flg)) sb.AppendLine("  " + s);
            }
        }

        Ops("PreOperations", "Permanent operations before the sequence:");
        foreach (var seq in graph.Descendants().Where(e => e.Name.LocalName == "Sequence"))
        {
            var comment = Child(seq, "Comment")?.Elements().FirstOrDefault()?.Value?.Trim();
            sb.AppendLine("Sequence" + (string.IsNullOrEmpty(comment) ? ":" : ": " + comment));

            var steps = seq.Descendants().Where(e => e.Name.LocalName == "Step").ToList();
            var stepName = steps.ToDictionary(s => s.Attribute("Number")?.Value ?? "", s => s.Attribute("Name")?.Value ?? "");
            foreach (var s in steps)
            {
                var line = $"  Step {s.Attribute("Number")?.Value} {s.Attribute("Name")?.Value}";
                if (s.Attribute("Init")?.Value == "true") line += " (initial step)";
                var max = s.Attribute("MaximumStepTime")?.Value;
                if (max != null) line += $", maximum step time {max}";
                sb.AppendLine(line);
                foreach (var a in s.Descendants().Where(e => e.Name.LocalName == "Action" && e.HasElements))
                {
                    var text = string.Concat(a.Elements().Where(e => e.Name.LocalName == "Token").Select(t => t.Attribute("Text")?.Value)).Trim();
                    var q = a.Attribute("Qualifier")?.Value;
                    var ev = a.Attribute("Event")?.Value;
                    sb.AppendLine($"    action {(ev != null ? ev + " " : "")}{q} {text}" + (a.Attribute("Interlock")?.Value == "true" ? " (only while the interlock is fulfilled)" : ""));
                }
                var il = s.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interlock")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
                var ilc = il != null ? CoilCondition(il, "IlCoil") : null;
                if (ilc != null && ilc != "TRUE") sb.AppendLine($"    interlock: {ilc}");
                var sv = s.Descendants().FirstOrDefault(e => e.Name.LocalName == "Supervision")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
                var svc = sv != null ? CoilCondition(sv, "SvCoil") : null;
                if (svc != null && svc != "TRUE") sb.AppendLine($"    supervision (fault when true): {svc}");
            }

            // Step -> transition -> step, from the connections
            var from = new Dictionary<string, string>();   // transition -> step before it
            var to = new Dictionary<string, string>();     // transition -> step after it
            foreach (var c in seq.Descendants().Where(e => e.Name.LocalName == "Connection"))
            {
                var f = Ref(Child(c, "NodeFrom"));
                var t = Ref(Child(c, "NodeTo"));
                if (f.Kind == "StepRef" && t.Kind == "TransitionRef") from[t.Number] = f.Number;
                if (f.Kind == "TransitionRef" && t.Kind == "StepRef") to[f.Number] = t.Number;
            }
            foreach (var tr in seq.Descendants().Where(e => e.Name.LocalName == "Transition"))
            {
                var no = tr.Attribute("Number")?.Value ?? "";
                var flg = tr.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
                var cond = flg != null ? CoilCondition(flg, "TrCoil") : null;
                string StepText(string? n) => n == null ? "?" : $"step {n} {stepName.GetValueOrDefault(n)}".TrimEnd();
                sb.AppendLine($"  Transition T{no} {tr.Attribute("Name")?.Value}: {StepText(from.GetValueOrDefault(no))} -> {StepText(to.GetValueOrDefault(no))} when {cond ?? "?"}");
            }
        }
        Ops("PostOperations", "Permanent operations after the sequence:");
        return sb.ToString().TrimEnd();
    }

    private static (string Kind, string Number) Ref(XElement? node)
    {
        var r = node?.Elements().FirstOrDefault();
        return (r?.Name.LocalName ?? "", r?.Attribute("Number")?.Value ?? "");
    }
}
