using System.Text.Json;

namespace PlcKi.Server.Ask;

/// <summary>A model the settings page offers for Ask, with its list price in US dollars per million tokens.</summary>
public sealed record AskModel(string Id, string Name, string Note, double Input, double Output, double CacheWrite, double CacheRead);

/// <summary>What Ask used in one calendar month (local time). Cost is an estimate from the token counts and list prices.</summary>
public sealed record AskMonth(string Month, int Questions, long Input, long Output, long CacheWrite, long CacheRead, double Cost);

/// <summary>
/// Counts what Ask uses, per month, in ask-usage.json in the data folder, so the settings page can show it and a monthly
/// limit can stop new questions. Every answer from the API is counted, also the rounds of a question that failed later.
/// The cost is an estimate: Anthropic's list prices per model (cache writes 1.25 × input, 5-minute cache), not the bill.
/// </summary>
public sealed class AskUsage
{
    /// <summary>
    /// The models the settings page offers (list prices, Claude API, November 2026). Ask asks for Anthropic's fallback
    /// model when one declines a question, so only models that support that are offered.
    /// </summary>
    public static readonly IReadOnlyList<AskModel> Models = new AskModel[]
    {
        new("claude-opus-5-5", "Claude Opus 5.5", "Recommended: good answers at a fair price.", 4, 20, 5, 0.20),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Faster, about half the price.", 2, 10, 2.5, 0.10),
        new("claude-fable-5-2", "Claude Fable 5.2", "The most capable, about twice the price.", 8, 40, 10, 0.20),
    };

    /// <summary>Models that may answer without being picked (the fallback after a declined question), for the price only.</summary>
    private static readonly IReadOnlyList<AskModel> Others = new AskModel[]
    {
        new("claude-opus-5", "Claude Opus 5", "", 5, 25, 6.25, 0.50),
        new("claude-opus-4-8", "Claude Opus 4.8", "", 5, 25, 6.25, 0.50),
        new("claude-sonnet-5", "Claude Sonnet 5", "", 2, 10, 2.5, 0.20),
        new("claude-fable-5-1", "Claude Fable 5.1", "", 10, 50, 12.5, 0.25),
    };

    /// <summary>A model not in the lists is priced like the most expensive one, so the estimate never comes out low.</summary>
    public static AskModel PriceOf(string? model)
    {
        var id = (model ?? "").Trim().Trim('"');
        return Models.Concat(Others).FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? Models.Concat(Others).MaxBy(m => m.Output)!;
    }

    public static double Cost(string? model, long input, long output, long cacheWrite, long cacheRead)
    {
        var p = PriceOf(model);
        return (input * p.Input + output * p.Output + cacheWrite * p.CacheWrite + cacheRead * p.CacheRead) / 1_000_000.0;
    }

    private const int KeepMonths = 24;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _file;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private List<AskMonth> _months;

    public AskUsage(string file, Func<DateTimeOffset>? now = null)
    {
        _file = file;
        _now = now ?? (() => DateTimeOffset.Now);
        try { _months = File.Exists(file) ? JsonSerializer.Deserialize<List<AskMonth>>(File.ReadAllText(file), Json) ?? new() : new(); }
        catch (Exception) { _months = new(); }     // a broken file starts the count again rather than stopping Ask
    }

    private string MonthKey => _now().ToString("yyyy-MM");

    public AskMonth ThisMonth()
    {
        lock (_lock) return _months.FirstOrDefault(m => m.Month == MonthKey) ?? new AskMonth(MonthKey, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>The months kept, newest first.</summary>
    public IReadOnlyList<AskMonth> Months() { lock (_lock) return _months.OrderByDescending(m => m.Month).ToList(); }

    /// <summary>One more question asked.</summary>
    public void Question() => Update(m => m with { Questions = m.Questions + 1 });

    /// <summary>One answer from the API (a question can take several: one per tool round).</summary>
    public void Add(string? model, long input, long output, long cacheWrite, long cacheRead) => Update(m => m with
    {
        Input = m.Input + input,
        Output = m.Output + output,
        CacheWrite = m.CacheWrite + cacheWrite,
        CacheRead = m.CacheRead + cacheRead,
        Cost = m.Cost + Cost(model, input, output, cacheWrite, cacheRead),
    });

    /// <summary>Why a new question may not be asked (the monthly limit is reached), or null. A limit of 0 or less is no limit.</summary>
    public string? LimitReached(double limitUsd)
    {
        if (limitUsd <= 0) return null;
        var m = ThisMonth();
        return m.Cost < limitUsd ? null
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Ask has used about ${m.Cost:0.00} this month, and the limit is ${limitUsd:0.##}. It works again next month, or an administrator raises the limit under Setup › Settings (Assistant).");
    }

    private void Update(Func<AskMonth, AskMonth> change)
    {
        lock (_lock)
        {
            var key = MonthKey;
            var now = _months.FirstOrDefault(m => m.Month == key) ?? new AskMonth(key, 0, 0, 0, 0, 0, 0);
            var list = _months.Where(m => m.Month != key).Append(change(now))
                .OrderByDescending(m => m.Month).Take(KeepMonths).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
                File.Move(tmp, _file, overwrite: true);
            }
            catch (IOException) { /* counted in memory; saved with the next answer */ }
            _months = list;
        }
    }
}
