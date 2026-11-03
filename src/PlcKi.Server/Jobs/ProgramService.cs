using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Server.Live;

namespace PlcKi.Server.Jobs;

/// <summary>
/// The program export the machine model was built from, for the Code and Logic pages:
/// block list, call tree, SCL sources and decoded LAD/FBD/GRAPH logic. Loaded once, on first use.
/// </summary>
public sealed class ProgramService
{
    private readonly string? _path;
    private readonly MachineModel _model;
    private readonly Lazy<(ProgramExport? Export, ExportPlc? Plc, string? Error)> _loaded;

    public ProgramService(PlcKiOptions opt, MachineModel model, Func<string, string> resolve)
    {
        _model = model;
        _path = string.IsNullOrWhiteSpace(opt.Export) ? null : resolve(opt.Export.Trim());
        _loaded = new(Load);
    }

    private (ProgramExport?, ExportPlc?, string?) Load()
    {
        if (_path == null) return (null, null, "No program export is set. Import the program (Setup → Program) to see the code.");
        try
        {
            var export = ProgramExport.Load(_path);
            // The PLC whose blocks the model talks about; otherwise the biggest one
            var plc = export.Plcs.FirstOrDefault(p => _model.Sequences.Any(s => s.Block != null && p.Block(s.Block) != null))
                      ?? export.Plcs.OrderByDescending(p => p.Blocks.Count).FirstOrDefault();
            return (export, plc, plc == null ? "The export has no PLC program." : null);
        }
        catch (Exception ex)
        {
            return (null, null, $"Cannot read the program export {_path}: {ex.Message}");
        }
    }

    public object Overview()
    {
        var (export, plc, error) = _loaded.Value;
        if (plc == null) return new { available = false, error };
        return new
        {
            available = true,
            project = export!.Project,
            archive = export.Archive,
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

    private static object Member(ExportMember m) => new
    {
        m.Name, m.Section, m.Datatype, m.Comment, m.StartValue,
        members = m.Members.Select(Member),
    };
}
