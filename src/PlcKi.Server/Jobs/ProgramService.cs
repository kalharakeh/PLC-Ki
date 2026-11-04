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

    /// <summary>Where a signal is used: blocks whose source or decoded logic mention it, with the first lines (read-only text search).</summary>
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
            var lines = text.Split('\n')
                .Select((l, i) => (No: i + 1, Text: l.TrimEnd('\r')))
                .Where(x => x.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            results.Add(new
            {
                block = b.Name, kind = b.Kind, language = b.Language, count = lines.Count,
                lines = lines.Take(4).Select(x => new { no = x.No, text = x.Text.Trim().Length > 160 ? x.Text.Trim()[..157] + "..." : x.Text.Trim() }),
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
            var b = plc.Blocks.FirstOrDefault(x => x.Name == db && x.Kind is "GlobalDB" or "InstanceDB");
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
            dbs = plc.Blocks.Where(b => b.Kind is "GlobalDB" or "InstanceDB").OrderBy(b => b.Number ?? int.MaxValue).ThenBy(b => b.Name)
                .Select(b => new { b.Name, b.Number, b.Kind, b.InstanceOf, b.Title, count = perDb.GetValueOrDefault(b.Name) }),
        };
    }

    private static object Member(ExportMember m) => new
    {
        m.Name, m.Section, m.Datatype, m.Comment, m.StartValue,
        members = m.Members.Select(Member),
    };
}
