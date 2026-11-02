using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcKi.Core.Model;

/// <summary>
/// Everything the app knows about one machine: which tags to read, what the steps mean,
/// how the equipment is interlocked and what each alarm means.
/// For the demo this file is written by hand; later the TIA archive importer generates it.
/// Conditions are short expressions over tag ids, for example "Level >= LevelA AND NOT AnyFault".
/// </summary>
public sealed class MachineModel
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Where this model came from (hand-written, or the archive it was generated from).</summary>
    public string? Source { get; set; }
    public List<TagDef> Tags { get; set; } = new();
    /// <summary>Tag ids shown as the main process values at the top of the page.</summary>
    public List<string> Overview { get; set; } = new();
    public List<SequenceDef> Sequences { get; set; } = new();
    public List<EquipmentDef> Equipment { get; set; } = new();
    public List<AlarmDef> Alarms { get; set; } = new();
    public List<ExchangeDef> Exchange { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MachineModel Load(string path)
    {
        var json = File.ReadAllText(path);
        var model = JsonSerializer.Deserialize<MachineModel>(json, JsonOptions)
                    ?? throw new InvalidDataException($"Model file {path} is empty.");
        model.Validate();
        return model;
    }

    /// <summary>Checks ids and expressions so a typo in the model fails at start-up, not live.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Tags)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) errors.Add("A tag has no id.");
            else if (!ids.Add(t.Id)) errors.Add($"Tag id '{t.Id}' is used twice.");
            if (string.IsNullOrWhiteSpace(t.Symbol)) errors.Add($"Tag '{t.Id}' has no symbol.");
            if (!TagTypes.IsKnown(t.Type)) errors.Add($"Tag '{t.Id}' has unknown type '{t.Type}'.");
        }

        void CheckTag(string? id, string where)
        {
            if (id != null && !ids.Contains(id)) errors.Add($"{where}: unknown tag '{id}'.");
        }

        void CheckExpr(string? expr, string where)
        {
            if (string.IsNullOrWhiteSpace(expr)) return;
            try
            {
                var node = Expressions.ExprParser.Parse(expr);
                foreach (var name in node.Identifiers())
                    if (!ids.Contains(name)) errors.Add($"{where}: unknown tag '{name}' in \"{expr}\".");
            }
            catch (Expressions.ExprException ex)
            {
                errors.Add($"{where}: {ex.Message}");
            }
        }

        void CheckText(string? text, string where)
        {
            if (text == null) return;
            foreach (var name in TextTemplate.Placeholders(text))
                if (!ids.Contains(name)) errors.Add($"{where}: unknown tag '{{{name}}}' in text.");
        }

        foreach (var id in Overview) CheckTag(id, "overview");
        foreach (var s in Sequences)
        {
            CheckTag(s.StepTag, $"sequence {s.Id}");
            CheckTag(s.StepTimeTag, $"sequence {s.Id}");
            CheckTag(s.HaltedTag, $"sequence {s.Id}");
            foreach (var h in s.HaltReasons) { CheckExpr(h.Expr, $"sequence {s.Id} halt reason"); CheckText(h.Text, $"sequence {s.Id} halt reason"); }
            foreach (var st in s.Steps)
            {
                CheckText(st.Does, $"step {st.No}");
                foreach (var tr in st.Next)
                {
                    if (!s.Steps.Any(x => x.No == tr.To)) errors.Add($"step {st.No}: next step {tr.To} does not exist.");
                    foreach (var c in tr.Conditions) { CheckExpr(c.Expr, $"step {st.No} -> {tr.To}"); CheckText(c.Text, $"step {st.No} -> {tr.To}"); }
                }
            }
        }
        foreach (var e in Equipment)
        {
            CheckExpr(e.Request, $"equipment {e.Id} request");
            CheckExpr(e.Command, $"equipment {e.Id} command");
            foreach (var p in e.Permits) { CheckExpr(p.Expr, $"equipment {e.Id} permit"); CheckText(p.Text, $"equipment {e.Id} permit"); }
            foreach (var st in e.States) { CheckExpr(st.When, $"equipment {e.Id} state"); CheckText(st.Text, $"equipment {e.Id} state"); }
            foreach (var v in e.Values) CheckTag(v, $"equipment {e.Id}");
        }
        foreach (var a in Alarms) CheckTag(a.Tag, $"alarm {a.Id}");
        foreach (var x in Exchange) CheckTag(x.Tag, "exchange");

        if (errors.Count > 0)
            throw new InvalidDataException("Machine model has errors:\n  " + string.Join("\n  ", errors));
    }
}

public sealed class TagDef
{
    /// <summary>Short name used in expressions and texts, e.g. "Level".</summary>
    public string Id { get; set; } = "";
    /// <summary>Full TIA symbol, e.g. "\"Station\".LT101.Value".</summary>
    public string Symbol { get; set; } = "";
    /// <summary>Bool, Int, DInt, Real or Time.</summary>
    public string Type { get; set; } = "Bool";
    public string? Label { get; set; }
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    /// <summary>Plain-English words for a Bool, e.g. "pressed" / "released".</summary>
    public string? TrueText { get; set; }
    public string? FalseText { get; set; }
}

public static class TagTypes
{
    public static readonly string[] Known = { "Bool", "Int", "DInt", "Real", "LReal", "Time" };
    public static bool IsKnown(string type) => Known.Contains(type);
}

public sealed class SequenceDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Block that runs it, shown as the location, e.g. "FB_BatchSequence (\"Station\".Seq)".</summary>
    public string? Block { get; set; }
    public string StepTag { get; set; } = "";
    public string? StepTimeTag { get; set; }
    public string? HaltedTag { get; set; }
    /// <summary>Checked when the sequence is halted, to say why.</summary>
    public List<CondDef> HaltReasons { get; set; } = new();
    public List<StepDef> Steps { get; set; } = new();
}

public sealed class StepDef
{
    public int No { get; set; }
    public string Name { get; set; } = "";
    /// <summary>What the machine does in this step (plain English).</summary>
    public string? Does { get; set; }
    /// <summary>Optional note on the step's time limit and the alarm it raises.</summary>
    public string? Limit { get; set; }
    public List<TransitionDef> Next { get; set; } = new();
}

public sealed class TransitionDef
{
    public int To { get; set; }
    /// <summary>All of these must be true to move on.</summary>
    public List<CondDef> Conditions { get; set; } = new();
}

public sealed class CondDef
{
    public string Expr { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class EquipmentDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>valve, motor, drive, heater, ...</summary>
    public string? Kind { get; set; }
    /// <summary>Block or instance that controls it.</summary>
    public string? Location { get; set; }
    /// <summary>Expression: the program asks for it to run / open.</summary>
    public string? Request { get; set; }
    /// <summary>Expression: the output to the field device is on.</summary>
    public string? Command { get; set; }
    /// <summary>Interlocks: all must be true for the command to follow the request.</summary>
    public List<CondDef> Permits { get; set; } = new();
    /// <summary>First matching state wins. Level: ok, info, idle, warn, fault.</summary>
    public List<StateDef> States { get; set; } = new();
    /// <summary>Extra tag ids to show next to the device.</summary>
    public List<string> Values { get; set; } = new();
}

public sealed class StateDef
{
    public string When { get; set; } = "TRUE";
    public string Text { get; set; } = "";
    public string Level { get; set; } = "idle";
}

public sealed class AlarmDef
{
    public string Id { get; set; } = "";
    public string Tag { get; set; } = "";
    /// <summary>FAULT or WARNING.</summary>
    public string Severity { get; set; } = "FAULT";
    public string Text { get; set; } = "";
    /// <summary>Where in the program it is raised.</summary>
    public string? Location { get; set; }
    /// <summary>The program condition that raises it, in plain English.</summary>
    public string? Condition { get; set; }
    public List<string> Causes { get; set; } = new();
    public List<string> Fix { get; set; } = new();
}

public sealed class ExchangeDef
{
    public string Tag { get; set; } = "";
    public string Partner { get; set; } = "";
    /// <summary>"in" = we receive it, "out" = we send it.</summary>
    public string Direction { get; set; } = "in";
    public string? Text { get; set; }
}
