using System.Text.RegularExpressions;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Server.Live;

namespace PlcKi.Server.Jobs;

/// <summary>
/// The program export the machine model was built from, for the Program page:
/// block list, call tree, SCL sources and decoded LAD/FBD/GRAPH logic. Loaded once, on first use.
/// </summary>
public sealed class ProgramService
{
    private string? _path;
    private readonly MachineModel _model;
    private Lazy<(ProgramExport? Export, ExportPlc? Plc, string? Error)> _loaded;

    public ProgramService(PlcKiOptions opt, MachineModel model, Func<string, string> resolve)
    {
        _model = model;
        _path = string.IsNullOrWhiteSpace(opt.Export) ? null : resolve(opt.Export.Trim());
        _loaded = new(Load);
    }

    /// <summary>The export file shown now.</summary>
    public string? Path => _path;

    /// <summary>The TIA project the shown export came from (its name in TIA), if one is loaded.</summary>
    public string? Project => _loaded.Value.Export?.Project;

    /// <summary>
    /// Shows another export from now on, without a restart ("Refresh from TIA"): the machine model stays, only code,
    /// logic, tags and addresses change. Returns the error when the file cannot be read; then nothing changes.
    /// </summary>
    public string? Reload(string path)
    {
        var next = new Lazy<(ProgramExport? Export, ExportPlc? Plc, string? Error)>(() => Load(path));
        if (next.Value.Plc == null) return next.Value.Error;
        lock (this)
        {
            _path = path;
            _allTags = null;
            _loaded = next;
        }
        return null;
    }

    private (ProgramExport?, ExportPlc?, string?) Load() => Load(_path);

    private (ProgramExport?, ExportPlc?, string?) Load(string? file)
    {
        if (file == null) return (null, null, "No program export is set. Import the program (Setup → Program) to see the code.");
        try
        {
            var export = ProgramExport.Load(file);
            // The PLC whose blocks the model talks about; otherwise the biggest one
            var plc = export.Plcs.FirstOrDefault(p => _model.Sequences.Any(s => s.Block != null && p.Block(s.Block) != null))
                      ?? export.Plcs.OrderByDescending(p => p.Blocks.Count).FirstOrDefault();
            return (export, plc, plc == null ? "The export has no PLC program." : null);
        }
        catch (Exception ex)
        {
            return (null, null, $"Cannot read the program export {file}: {ex.Message}");
        }
    }

    /// <summary>The loaded export and the PLC the model is about (for the Hardware page's tag names).</summary>
    public (ProgramExport? Export, ExportPlc? Plc, string? Error) Loaded => _loaded.Value;

    public object Overview()
    {
        var (export, plc, error) = _loaded.Value;
        if (plc == null) return new { available = false, error };
        return new
        {
            available = true,
            project = export!.Project,
            archive = export.Archive,
            exportedAt = export.ExportedAt,
            plc = plc.Name,
            cpu = plc.Cpu,
            ip = plc.IpAddresses,
            blocks = plc.Blocks.Select(b => new
            {
                b.Name, b.Kind, b.Number, b.Language, b.Title, b.Comment, b.InstanceOf,
                languages = b.NetworkLanguages.Distinct(),
                calls = b.Calls.Select(c => new { c.BlockType, c.Block, c.Instance }),
                hasSource = b.SclSource != null,
                hasLogic = b.DecodedLogic != null,
            }),
            types = plc.Types.Select(t => new { t.Name, t.Kind }),
        };
    }

    public object? Block(string name)
    {
        var (_, plc, _) = _loaded.Value;
        var b = plc?.Block(name);
        if (b == null) return null;
        return new
        {
            b.Name, b.Kind, b.Number, b.Language, b.Title, b.Comment, b.InstanceOf,
            languages = b.NetworkLanguages.Distinct(),
            calls = b.Calls.Select(c => new { c.BlockType, c.Block, c.Instance }),
            calledBy = plc!.Blocks.Where(o => o.Calls.Any(c => c.Block == b.Name)).Select(o => o.Name),
            @interface = b.Interface.Select(Member),
            source = b.SclSource,
            logic = b.DecodedLogic,
        };
    }

    /// <summary>
    /// Does this line write <paramref name="symbol"/>: "x := ..." or a set/reset "IF ... THEN x := TRUE; END_IF;"?
    /// Same rule as writes() on the Program page. A call parameter ("In := x,") does not count.
    /// </summary>
    public static bool Writes(string line, string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        symbol = symbol.Trim();
        // A bare name ("FaultNotOpen", "A02_XV101_NotOpen") also matches a local (#FaultNotOpen) or the last part of a
        // path ("DB_Alarms".A02_XV101_NotOpen)
        var bare = !symbol.StartsWith('"') && !symbol.StartsWith('#');
        var before = bare ? @"(?:(?:""[^""]+""|#?\w+)\.)*[#""]?" : "";
        var after = bare ? @"""?" : "";
        return Regex.IsMatch(line.Trim(), @"(^|\bTHEN\s+)" + before + Regex.Escape(symbol) + after + @"(?![\w.\[])\s*:=(?!.*,\s*$)", RegexOptions.IgnoreCase);
    }

    private static readonly Regex Opener = new(@"\b(IF|CASE|FOR|WHILE|REPEAT)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Closer = new(@"\bEND_(IF|CASE|FOR|WHILE|REPEAT)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Is the assignment on line <paramref name="index"/> only done when a condition holds: inside an IF, CASE or
    /// loop, or after THEN on the same line? An unconditional "x := ..." is written each time the block runs, so a
    /// change from outside is overwritten at once; a conditional one only when its condition is true.
    /// </summary>
    public static bool Conditional(IReadOnlyList<string> lines, int index)
    {
        static string Code(string l)
        {
            l = Regex.Replace(l, @"\(\*.*?\*\)|'[^']*'|""[^""]*""", " ");   // comments on one line, texts, quoted names
            var c = l.IndexOf("//", StringComparison.Ordinal);
            return c >= 0 ? l[..c] : l;
        }
        if (index < 0 || index >= lines.Count) return false;
        if (Regex.IsMatch(Code(lines[index]), @"\bTHEN\b", RegexOptions.IgnoreCase)) return true;
        var depth = 0;
        var inComment = false;
        for (var i = 0; i < index; i++)
        {
            var l = lines[i];
            if (inComment) { var e = l.IndexOf("*)", StringComparison.Ordinal); if (e < 0) continue; l = l[(e + 2)..]; inComment = false; }
            var s = l.IndexOf("(*", StringComparison.Ordinal);
            if (s >= 0 && l.IndexOf("*)", s, StringComparison.Ordinal) < 0) { l = l[..s]; inComment = true; }
            var code = Code(l);
            depth += Opener.Matches(code).Count - Closer.Matches(code).Count;
        }
        return depth > 0;
    }

    /// <summary>
    /// Where a signal is used: blocks whose source or decoded logic mention it, with the first lines (read-only text search).
    /// Each line says whether it writes the signal; lines that write it are always included, so "where is it set" can be found.
    /// </summary>
    public object Search(string query, int maxBlocks = 60)
    {
        var (_, plc, error) = _loaded.Value;
        var q = (query ?? "").Trim();
        if (plc == null) return new { available = false, error, results = Array.Empty<object>() };
        if (q.Length < 2) return new { available = true, error = (string?)null, results = Array.Empty<object>() };
        var results = new List<object>();
        foreach (var b in plc.Blocks)
        {
            var text = b.SclSource ?? b.DecodedLogic;
            if (text == null || text.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var all = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            var lines = all
                .Select((l, i) => (No: i + 1, Text: l))
                .Where(x => x.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.No, x.Text, Writes: Writes(x.Text, q)))
                .ToList();
            var shown = lines.Where((x, i) => i < 4 || x.Writes);
            results.Add(new
            {
                block = b.Name, kind = b.Kind, language = b.Language, count = lines.Count,
                lines = shown.Select(x => new
                {
                    no = x.No, text = x.Text.Trim().Length > 160 ? x.Text.Trim()[..157] + "..." : x.Text.Trim(), writes = x.Writes,
                    conditional = x.Writes ? Conditional(all, x.No - 1) : (bool?)null,
                }),
            });
            if (results.Count >= maxBlocks) break;
        }
        return new { available = true, error = (string?)null, results };
    }

    private (ExportPlc Plc, List<ExportTag> Tags)? _allTags;
    private List<ExportTag> AllTags(ExportPlc plc)
    {
        if (_allTags is { } t && ReferenceEquals(t.Plc, plc)) return t.Tags;
        var tags = ProgramExport.Tags(plc);
        _allTags = (plc, tags);
        return tags;
    }

    /// <summary>
    /// The Tags page: PLC tag tables (name, address, type, comment) and the data blocks with how many values each
    /// holds. With <paramref name="db"/>, the readable values of that data block, with their full symbols.
    /// </summary>
    public object Tags(string? db)
    {
        var (_, plc, error) = _loaded.Value;
        if (plc == null) return new { available = false, error };
        if (!string.IsNullOrEmpty(db))
        {
            var b = plc.Blocks.FirstOrDefault(x => x.Name == db && x.Kind is "GlobalDB" or "InstanceDB" or "GVL" or "PRG");
            if (b == null) return new { available = false, error = $"Data block {db} is not in the program export." };
            return new
            {
                available = true, db = b.Name, b.Number, b.Kind, b.InstanceOf, b.Title, b.Comment,
                values = AllTags(plc).Where(t => t.Source == b.Name)
                    .Select(t => new { symbol = t.Symbol, datatype = t.Datatype, type = t.ModelType, comment = t.Comment }),
            };
        }
        var perDb = AllTags(plc).GroupBy(t => t.Source).ToDictionary(g => g.Key, g => g.Count());
        return new
        {
            available = true,
            tables = plc.PlcTags.GroupBy(t => t.Table).OrderBy(g => g.Key).Select(g => new
            {
                name = g.Key,
                tags = g.Select(t => new { t.Name, symbol = $"\"{t.Name}\"", t.Address, datatype = t.Datatype, type = ProgramExport.ModelType(t.Datatype), t.Comment }),
            }),
            dbs = plc.Blocks.Where(b => b.Kind is "GlobalDB" or "InstanceDB" or "GVL" or "PRG").OrderBy(b => b.Number ?? int.MaxValue).ThenBy(b => b.Name)
                .Select(b => new { b.Name, b.Number, b.Kind, b.InstanceOf, b.Title, count = perDb.GetValueOrDefault(b.Name) }),
        };
    }

    /// <summary>
    /// The HMI panels in the export, for the Settings tab: every value an operator sets on a screen (where, the text
    /// next to it, the PLC symbol, its type for live reading), the buttons that set PLC values, and for each model
    /// setting where it is on the panel (matched by PLC symbol).
    /// </summary>
    public object Hmi()
    {
        var (export, plc, error) = _loaded.Value;
        if (export == null || plc == null) return new { available = false, error };
        if (export.Hmis.Count == 0)
            return new { available = false, error = (string?)"This program export has no HMI. Export the program again with the current TIA helper (Setup → Program) to read the panel's screens." };
        var types = new Dictionary<string, (string Symbol, string? Type, string? Comment)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in AllTags(plc)) types.TryAdd(ProgramExport.NormalizeSymbol(t.Symbol), (t.Symbol, t.ModelType, t.Comment));
        var modelIds = _model.Tags.GroupBy(t => ProgramExport.NormalizeSymbol(t.Symbol), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        string Where(HmiDevice h, HmiField f)
        {
            var screen = h.Screens.FirstOrDefault(s => s.Name == f.Screen);
            return (screen?.Kind == "Popup" ? "Pop-up " : "Screen ") + f.Screen + (f.Label != null ? " > " + f.Label : "");
        }
        object Field(HmiDevice h, HmiField f)
        {
            var key = ProgramExport.NormalizeSymbol(f.PlcSymbol!);
            var known = types.TryGetValue(key, out var x);
            var screen = h.Screens.FirstOrDefault(s => s.Name == f.Screen);
            return new
            {
                hmi = h.Name, screen = f.Screen, screenNumber = screen?.Number, screenKind = screen?.Kind, folder = screen?.Folder,
                label = f.Label ?? f.Name, where = Where(h, f), kind = f.Kind, action = f.Action,
                symbol = known ? x.Symbol : f.PlcSymbol, type = known ? x.Type : null, comment = known ? x.Comment : null,
                unit = f.Unit, textList = f.TextList, authorization = f.Authorization,
                modelTag = modelIds.GetValueOrDefault(key),
            };
        }
        var bySetting = new Dictionary<string, string>();
        foreach (var st in _model.Settings)
        {
            var symbol = _model.Tags.FirstOrDefault(t => t.Id == st.Tag)?.Symbol;
            if (symbol == null) continue;
            var key = ProgramExport.NormalizeSymbol(symbol);
            var places = export.Hmis.SelectMany(h => h.OperatorSettings().Where(f => ProgramExport.NormalizeSymbol(f.PlcSymbol!).Equals(key, StringComparison.OrdinalIgnoreCase)).Select(f => Where(h, f)))
                .Distinct().ToList();
            if (places.Count > 0) bySetting[st.Tag] = string.Join("; ", places.Take(3));
        }
        return new
        {
            available = true,
            panels = export.Hmis.Select(h => new
            {
                h.Name, h.Device, h.Type, h.Kind,
                screens = h.Screens.Count(s => s.Kind != "Template"), tags = h.Tags.Count, textLists = h.TextLists.Count, alarms = h.Alarms.Count,
                problems = h.Problems.Take(20),
            }),
            settings = export.Hmis.SelectMany(h => h.OperatorSettings().Select(f => Field(h, f))),
            commands = export.Hmis.SelectMany(h => h.OperatorCommands().Select(f => Field(h, f))),
            alarms = export.Hmis.SelectMany(h => h.Alarms.Select(a => new { hmi = h.Name, a.Id, a.Name, a.Text, a.PlcSymbol, a.Bit, a.AlarmClass, a.InfoText })),
            bySetting,
        };
    }

    private static object Member(ExportMember m) => new
    {
        m.Name, m.Section, m.Datatype, m.Comment, m.StartValue,
        members = m.Members.Select(Member),
    };
}
