namespace PlcKi.Core.Import;

/// <summary>The PLC brands PLC-Ki knows. The machine model, status engine and Ask are the same for every brand.</summary>
public static class PlcBrands
{
    public const string Siemens = "Siemens";
    public const string Beckhoff = "Beckhoff";

    /// <summary>What the brand's program files and live connection are called, for messages and the import prompt.</summary>
    public static string Describe(string brand) => brand switch
    {
        Beckhoff => "Beckhoff TwinCAT 3 (IEC 61131-3)",
        _ => "Siemens S7-1500/S7-1200 (TIA Portal)",
    };
}

/// <summary>
/// Turns one brand's program files into the brand-neutral <see cref="ProgramExport"/> (blocks, interfaces,
/// source, tags). Everything after this step (digest, import, model check, live status) works the same for every brand.
/// </summary>
public interface IProgramReader
{
    string Brand { get; }
    /// <summary>True when <paramref name="folder"/> holds this brand's program files.</summary>
    bool CanRead(string folder);
    ProgramExport Read(string folder);
}

/// <summary>The readers PLC-Ki tries, in order, for a program folder.</summary>
public static class ProgramReaders
{
    public static readonly IReadOnlyList<IProgramReader> All = new IProgramReader[]
    {
        new TiaExportReader(),
        new TwinCatProject(),
    };

    public static ProgramExport Read(string folder)
    {
        foreach (var r in All)
            if (r.CanRead(folder)) return r.Read(folder);
        throw new FileNotFoundException(
            $"No program found in {folder}. PLC-Ki reads a TiaImporter export (manifest.json) or a TwinCAT 3 project (.plcproj with .TcPOU files).");
    }

    private sealed class TiaExportReader : IProgramReader
    {
        public string Brand => PlcBrands.Siemens;
        public bool CanRead(string folder) => ProgramExport.IsTiaExport(folder);
        public ProgramExport Read(string folder) => ProgramExport.LoadTiaExport(folder);
    }
}
