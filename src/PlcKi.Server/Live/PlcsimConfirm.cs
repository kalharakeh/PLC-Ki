using PlcKi.Core.Status;
using PlcKi.Server.Hardware;
using PlcKi.Server.Jobs;

namespace PlcKi.Server.Live;

/// <summary>
/// Is the PLC PLC-Ki reads the TIA helper's PLCSIM instance? Shared by "Change a value", "Hold on PLCSIM" and the
/// badge, so they all mean the same by "PLCSIM" (the rules are ChangeRules.PlcsimNotConfirmed).
/// </summary>
internal static class PlcsimConfirm
{
    /// <summary>NotConfirmed is null when PLCSIM is confirmed; otherwise why not, in words that follow "TIA helper: ".</summary>
    internal static async Task<(string? NotConfirmed, string? Instance)> CheckAsync(PlcKiOptions opt, TiaConnection tia, LiveState live, bool fresh, CancellationToken ct)
    {
        var plc = opt.Plc.Address.Trim();
        if (tia.NotSetUp() != null) return ("none is set up, so PLC-Ki cannot ask it", null);
        var r = await HardwareEndpoints.PlcsimAsync(opt, tia, fresh, ct);
        string? S(string key) => r.GetValueOrDefault(key) as string;   // keys are there only when they have a value
        var applies = r.GetValueOrDefault("applies") is true;
        var found = r.GetValueOrDefault("found") is true;
        string? why = null;
        if (!applies)
        {
            why = S("plcsimIp") is { } ip ? $"its PLCSIM CPU is at {ip}, not {plc}"
                : r.ContainsKey("plcsimIp") ? "it has no PLCSIM address"   // the helper answered without one
                : (S("why") ?? "").Replace("The TIA helper does not answer, so PLC-Ki cannot tell whether this is PLCSIM:", "it does not answer:");
        }
        var cpu = live.Cpu;
        var plcState = cpu != null && cpu.Error == null && DateTimeOffset.Now - cpu.Time < TimeSpan.FromSeconds(30) ? cpu.State : null;
        return (ChangeRules.PlcsimNotConfirmed(plc, applies, found, S("name"), why, S("error"), plcState, S("state")), S("name"));
    }
}
