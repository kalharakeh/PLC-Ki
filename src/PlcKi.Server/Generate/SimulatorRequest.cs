using System.Text;
using PlcKi.Core.Import;
using PlcKi.Core.Model;

namespace PlcKi.Server.Generate;

/// <summary>
/// The block generator request for a machine simulator, made from the machine model and the program: which devices
/// must answer, which settings must be valid, where to call it. Everything the Helium PLCSIM run taught is in here,
/// so a new import can run in PLCSIM without anyone writing the request by hand.
/// </summary>
public static class SimulatorRequest
{
    public sealed record Result(string Name, string Request, string? Edit, string Note);

    public static Result Build(MachineModel model, ExportPlc? plc)
    {
        var map = plc != null ? FindInputMapping(plc) : null;
        var safety = plc?.Blocks.Any(b => b.IsSafety) == true;
        var sb = new StringBuilder();
        sb.AppendLine($"Write a machine simulator so this program ({model.Name}) runs in PLCSIM without hardware, cycle after cycle, with nobody at the machine.");
        sb.AppendLine(map != null
            ? $"Make a new SCL FB \"FB_MachineSim\" with instance DB \"MachineSim_DB\" and call it at the very end of \"{map}\", where the program maps its inputs. Keep everything else in that block exactly as it is."
            : "Make a new SCL FB \"FB_MachineSim\" with instance DB \"MachineSim_DB\". Call it at the end of the block where the program maps its physical inputs (the input image), or first in the main cycle OB if there is none.");
        sb.AppendLine("The FB overwrites what the program reads from the field with what the real machine would answer to the program's outputs, after realistic delays.");
        sb.AppendLine();
        sb.AppendLine("Auto operator (static AutoRun, default TRUE): select the automatic/production mode, pulse Reset when the machine is stopped by a fault or after start-up, pulse Start when it is ready and idle, so the next cycle starts on its own after the previous one ended. Fill in what an operator would enter (product, barcode, recipe) the way the program expects it.");
        sb.AppendLine("Handshakes: a device that gets a job or command number and a start pulse echoes the number, reports busy/moving while it works, then the end position or done. Do not depend on the start bit staying on: it is often a pulse, and the job number keeps its last value after the job.");
        if (model.Equipment.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Devices that must answer (from PLC-Ki's model of this program):");
            foreach (var e in model.Equipment.Take(60))
                sb.AppendLine($"- {e.Name}" + (e.Kind != null ? $" ({e.Kind})" : "") + (e.Location != null ? $", in {e.Location}" : ""));
        }
        var settings = model.Settings.Where(s => s.Valid != null).ToList();
        sb.AppendLine();
        sb.AppendLine("Settings: the HMI and recipe values the sequences depend on are often 0 in a fresh PLCSIM. When one of them is 0 or makes a step impossible (an empty tolerance window, an endless delay), the auto operator sets a value a real operator would use, and keeps delays between cycles short (a few seconds).");
        foreach (var s in settings)
            sb.AppendLine($"- {Label(model, s.Tag)}: must satisfy {s.Valid}" + (s.Problem != null ? $" (otherwise {s.Problem})" : ""));
        sb.AppendLine("Measurements: give values inside the windows the program checks (calibration, background, pressure, speed), with a little noise but never outside the limits, unless a fault injection bit asks for it.");
        sb.AppendLine("Start-up waits: if the program waits a long time after start-up (warm-up, calibration delay), add a static SkipStartupWait (default TRUE) that shortens it.");
        sb.AppendLine();
        sb.AppendLine("Fault injection: static Bools in MachineSim_DB, all FALSE by default, one per device fault the alarms describe (device not ready, no feedback, value out of range, measurement fails). Each one makes the matching alarm come; switching it off and pressing Reset clears it.");
        sb.AppendLine(safety
            ? "Safety: this PLC has a safety program. Never write F-blocks, F-I/O or F-data. The safety inputs are set in the PLCSIM copy by the engineer; the simulator only reads the safety program's result."
            : "Safety: E-stop and guard inputs read healthy (released, closed) unless a fault injection bit opens them.");
        sb.AppendLine("Mark the call region \"PLCSIM ONLY: remove before commissioning\". The review lists everything the simulator overwrites and how to remove it.");
        var note = map != null
            ? $"Calls the simulator at the end of \"{map}\"; that block is put under \"Change existing blocks\"."
            : "No input-mapping block found: Ki picks where to call it.";
        return new Result("machine-sim", sb.ToString().TrimEnd(), map, note);
    }

    private static string Label(MachineModel m, string id) => m.Tags.FirstOrDefault(t => t.Id == id) is { } t ? $"{t.Label ?? id} ({t.Symbol})" : id;

    /// <summary>The FC or FB, called from a cycle OB, that copies the physical inputs into the program's own tags.</summary>
    public static string? FindInputMapping(ExportPlc plc)
    {
        static bool Looks(ExportBlock b)
        {
            var s = (b.Name + " " + b.Title + " " + b.Comment).ToLowerInvariant();
            return s.Contains("input") || s.Contains("i/o") || s.Contains("io image") || s.Contains("image") || s.Contains("mapping") || s.Contains("map io");
        }
        // Blocks reached from the cycle OBs, in call order, first one that looks like the input image
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<ExportBlock>();
        void Walk(ExportBlock b, int depth)
        {
            if (depth > 4 || !seen.Add(b.Name)) return;
            order.Add(b);
            foreach (var c in b.Calls) if (plc.Block(c.Block) is { } t) Walk(t, depth + 1);
        }
        foreach (var ob in plc.Blocks.Where(b => b.Kind == "OB" && (b.Number == 1 || b.Name.Contains("Main", StringComparison.OrdinalIgnoreCase))))
            Walk(ob, 0);
        return order.FirstOrDefault(b => b.Kind is "FC" or "FB" && !b.IsSafety && Looks(b))?.Name;
    }
}
