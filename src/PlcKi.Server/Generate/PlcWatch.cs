using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcKi.Server.Generate;

/// <summary>CPU state and active alarms at one moment, read from the PLC.</summary>
public sealed record PlcSnapshot(string State, Dictionary<ulong, string> Alarms);

/// <summary>
/// Watches the PLC (PLCSIM) after a download, read-only through S7CommPlus like the rest of PLC-Ki: is the CPU in
/// RUN, and which alarms came up that were not there before. What it finds goes back to Claude as runtime problems.
/// </summary>
public sealed class PlcWatch
{
    private readonly string _address;
    private readonly Action<string> _log;

    public PlcWatch(string address, Action<string> log)
    {
        _address = address;
        _log = log;
    }

    public async Task<PlcSnapshot> SnapshotAsync(CancellationToken ct)
    {
        await using var client = await ConnectAsync(ct);
        return await ReadAsync(client, ct);
    }

    /// <summary>Polls for <paramref name="duration"/> and lists what went wrong compared with <paramref name="before"/>.</summary>
    public async Task<List<string>> WatchAsync(PlcSnapshot? before, TimeSpan duration, CancellationToken ct)
    {
        var problems = new List<string>();
        var seen = new HashSet<ulong>(before?.Alarms.Keys ?? Enumerable.Empty<ulong>());
        string? lastState = null;
        var end = DateTime.UtcNow + duration;
        await using var client = await ConnectAsync(ct);
        while (true)
        {
            var now = await ReadAsync(client, ct);
            if (now.State != lastState)
            {
                _log($"  CPU: {now.State}");
                if (now.State != "Run") problems.Add($"The CPU is in {now.State} instead of RUN.");
                lastState = now.State;
            }
            foreach (var (id, text) in now.Alarms)
                if (seen.Add(id))
                {
                    _log($"  new alarm: {text}");
                    problems.Add($"Alarm came up: {text}");
                }
            if (DateTime.UtcNow >= end) break;
            await Task.Delay(2000, ct);
        }
        return problems.Distinct().ToList();
    }

    private async Task<S7CommPlusClient> ConnectAsync(CancellationToken ct)
    {
        var client = new S7CommPlusClient(new S7CommPlusClientOptions
        {
            Address = _address,
            Password = Environment.GetEnvironmentVariable("PLCKI_PLC_PASSWORD") ?? "",
            ConnectTimeout = TimeSpan.FromSeconds(5),
            RequestTimeout = TimeSpan.FromSeconds(5),
            AutoReconnect = false,
            // WriteEnabled stays at its default (false): watching is read-only
            SecurityMode = S7CommPlusSecurityMode.Auto,
        });
        await client.ConnectAsync(ct);
        return client;
    }

    private static async Task<PlcSnapshot> ReadAsync(S7CommPlusClient client, CancellationToken ct)
    {
        var state = await client.GetCpuStateAsync(ct);
        var alarms = new Dictionary<ulong, string>();
        try
        {
            foreach (var a in await client.GetActiveAlarmsAsync(ct))
            {
                var texts = a.AlarmTexts ?? a.AlarmTextsByLanguage.Values.FirstOrDefault();
                var text = string.IsNullOrWhiteSpace(texts?.AlarmText) ? $"alarm {a.CpuAlarmId}" : texts!.AlarmText.Trim();
                if (!string.IsNullOrEmpty(a.AlarmClassName)) text += $" ({a.AlarmClassName})";
                alarms[a.CpuAlarmId] = text;
            }
        }
        catch (Exception) { /* some CPUs have no alarm service; the state is still useful */ }
        return new PlcSnapshot(state.OperatingState.ToString(), alarms);
    }
}
