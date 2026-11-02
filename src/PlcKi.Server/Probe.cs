using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Server.Live;

namespace PlcKi.Server;

/// <summary>
/// One-shot connection test: connect, find every tag, read once, print a table and the status text.
/// Exit code 0 = connected and all tags read, 1 = connected but some tags failed, 3 = no connection.
/// </summary>
public static class Probe
{
    public static int Run(MachineModel model, IPlcSource source)
    {
        Console.WriteLine($"PLC-Ki probe (read-only): {source.Kind} {source.Address}, model \"{model.Name}\", {model.Tags.Count} tags.");
        Dictionary<string, string> missing;
        try
        {
            missing = source.Connect(model.Tags);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("NOT CONNECTED: " + ex.Message);
            Console.WriteLine("Check: ping the PLC address, the CPU is in RUN, and the address in appsettings.json.");
            return 3;
        }

        Snapshot snap;
        try
        {
            snap = source.Read();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Connected, but reading failed: " + ex.Message);
            return 3;
        }
        finally
        {
            source.Disconnect();
        }
        foreach (var (id, why) in missing) snap.Errors.TryAdd(id, why);

        Console.WriteLine();
        Console.WriteLine($"{"Tag",-18} {"Symbol",-44} Value");
        Console.WriteLine(new string('-', 90));
        foreach (var t in model.Tags)
        {
            var text = snap.Errors.TryGetValue(t.Id, out var err)
                ? "ERROR: " + err
                : ValueFormat.Format(t, snap.Values.GetValueOrDefault(t.Id));
            Console.WriteLine($"{t.Id,-18} {t.Symbol,-44} {text}");
        }

        var engine = new StatusEngine(model);
        var status = engine.Evaluate(snap, new PlcLinkInfo("connected", source.Address, null, snap.Time, model.Tags.Count, snap.Errors.Count));
        Console.WriteLine();
        Console.WriteLine("What the machine is doing:");
        foreach (var line in status.Summary) Console.WriteLine("  " + line);
        Console.WriteLine();
        Console.WriteLine(snap.Errors.Count == 0
            ? $"OK: all {model.Tags.Count} tags read."
            : $"{model.Tags.Count - snap.Errors.Count} of {model.Tags.Count} tags read, {snap.Errors.Count} failed (see ERROR lines).");
        return snap.Errors.Count == 0 ? 0 : 1;
    }
}
