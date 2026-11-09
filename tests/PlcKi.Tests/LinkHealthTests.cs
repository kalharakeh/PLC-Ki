using Microsoft.Extensions.Logging.Abstractions;
using PlcKi.Core.Model;
using PlcKi.Server.Live;
using S7CommPlusDriver;
using Xunit;

/// <summary>
/// The PLC link stays up while pages ask for names the PLC does not have: those are remembered across reconnects,
/// looked up a few per poll, on their own connection, and a short drop does not flash "Not connected".
/// </summary>
public class LinkHealthTests
{
    private sealed record FakeTag(string Symbol);

    /// <summary>A stand-in for one extra-values connection: knows some names; can be told to break like a faulted driver client.</summary>
    private sealed class FakeLink : IExtraLink<FakeTag>
    {
        private readonly FakePlc _plc;
        public FakeLink(FakePlc plc) { _plc = plc; }
        public bool Broken { get; set; }
        public bool Disposed { get; private set; }
        public bool IsConnected => !Broken && !Disposed;

        public (FakeTag? Tag, string? NotFound) Lookup(string symbol)
        {
            Assert.False(Disposed || Broken, "a closed or faulted link must not be used again");
            _plc.Lookups.Add(symbol);
            if (_plc.BreakOn == symbol) { Broken = true; throw new IOException("timed out"); }
            return _plc.Names.Contains(symbol) ? (new FakeTag(symbol), null) : (null, $"PLC tag '{symbol}' could not be resolved.");
        }

        public IReadOnlyList<(object? Raw, string? Error)> Read(IReadOnlyList<FakeTag> tags)
        {
            Assert.False(Disposed || Broken, "a closed or faulted link must not be used again");
            if (_plc.BreakOnRead) { Broken = true; throw new IOException("read timed out"); }
            return tags.Select(t => ((object?)true, (string?)null)).ToList();
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakePlc
    {
        public HashSet<string> Names { get; } = new() { "\"DB_IO\".Start", "\"DB_IO\".Stop" };
        public List<string> Lookups { get; } = new();
        public List<FakeLink> Links { get; } = new();
        public string? BreakOn { get; set; }
        public bool BreakOnRead { get; set; }
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

        public ExtraReader<FakeTag> Reader() => new(() => { var l = new FakeLink(this); Links.Add(l); return l; }, NullLogger.Instance, () => Now);
    }

    private static List<TagDef> Defs(params string[] symbols) => symbols.Select(s => new TagDef { Id = s, Symbol = s, Type = "Bool" }).ToList();
    private static LookupBudget Plenty() => new(30, TimeSpan.FromSeconds(10));

    [Fact]
    public void A_name_the_plc_does_not_have_is_not_looked_up_again_after_a_reconnect()
    {
        var plc = new FakePlc();
        var reader = plc.Reader();
        var r = reader.Read(Defs("\"DB_IO\".Start", "#tempFlag"), Plenty());
        Assert.Equal(true, r["\"DB_IO\".Start"].Value);
        Assert.StartsWith("Not on the PLC", r["#tempFlag"].Error);

        // The extra link breaks on a read: closed, paused, then opened again
        plc.BreakOnRead = true;
        r = reader.Read(Defs("\"DB_IO\".Start"), Plenty());
        Assert.StartsWith("Extra values are paused", r["\"DB_IO\".Start"].Error);
        Assert.True(plc.Links[0].Disposed);
        plc.BreakOnRead = false;
        plc.Now += TimeSpan.FromSeconds(6);

        r = reader.Read(Defs("\"DB_IO\".Start", "#tempFlag"), Plenty());
        Assert.Equal(2, plc.Links.Count);
        Assert.Equal(true, r["\"DB_IO\".Start"].Value);
        Assert.StartsWith("Not on the PLC", r["#tempFlag"].Error);
        Assert.Single(plc.Lookups, "#tempFlag");             // asked once, not again on the new connection
        Assert.Single(plc.Lookups, "\"DB_IO\".Start");       // the found tag is kept too
    }

    [Fact]
    public void A_new_program_or_enough_time_lets_a_missing_name_be_asked_again()
    {
        var plc = new FakePlc();
        var reader = plc.Reader();
        reader.Read(Defs("\"DB_New\".X"), Plenty());
        reader.Read(Defs("\"DB_New\".X"), Plenty());
        Assert.Single(plc.Lookups);

        plc.Names.Add("\"DB_New\".X");                       // downloaded, and Refresh from TIA
        reader.ForgetProgram();
        var r = reader.Read(Defs("\"DB_New\".X"), Plenty());
        Assert.Equal(true, r["\"DB_New\".X"].Value);

        reader.Read(Defs("#gone"), Plenty());
        plc.Now += reader.Cache.RetryNotFoundAfter + TimeSpan.FromSeconds(1);
        reader.Read(Defs("#gone"), Plenty());
        Assert.Equal(2, plc.Lookups.Count(s => s == "#gone"));
    }

    [Fact]
    public void A_lookup_that_breaks_the_link_closes_it_and_waits_before_opening_a_new_one()
    {
        var plc = new FakePlc { BreakOn = "\"DB_Slow\".X" };
        var reader = plc.Reader();
        var r = reader.Read(Defs("\"DB_Slow\".X", "\"DB_IO\".Start", "\"DB_IO\".Stop"), Plenty());

        // No call on the faulted link after the failure (FakeLink asserts it), and every name says why
        Assert.True(plc.Links[0].Disposed);
        Assert.All(r.Values, v => Assert.StartsWith("Extra values are paused", v.Error));
        Assert.Contains("looking up \"DB_Slow\".X", reader.Problem);

        // Paused: no new connection within the wait
        plc.Now += TimeSpan.FromSeconds(2);
        reader.Read(Defs("\"DB_IO\".Start"), Plenty());
        Assert.Single(plc.Links);

        // After the wait one new connection; the failed name is asked again only then (it was not cached as missing)
        plc.BreakOn = null;
        plc.Now += TimeSpan.FromSeconds(4);
        r = reader.Read(Defs("\"DB_Slow\".X", "\"DB_IO\".Start"), Plenty());
        Assert.Equal(2, plc.Links.Count);
        Assert.Equal(true, r["\"DB_IO\".Start"].Value);
        Assert.StartsWith("Not on the PLC", r["\"DB_Slow\".X"].Error);
    }

    [Fact]
    public void Lookups_per_poll_are_limited_by_count_and_by_time()
    {
        var elapsed = TimeSpan.Zero;
        var budget = new LookupBudget(3, TimeSpan.FromMilliseconds(1500), () => elapsed);
        Assert.True(budget.TryTake());
        Assert.True(budget.TryTake());
        elapsed = TimeSpan.FromMilliseconds(1600);
        Assert.False(budget.TryTake());                      // out of time
        elapsed = TimeSpan.Zero;
        Assert.True(budget.TryTake());
        Assert.False(budget.TryTake());                      // out of lookups
        Assert.Equal(3, budget.Used);

        // Through the reader: two new names per poll, the rest in the next polls; known names cost nothing
        var plc = new FakePlc();
        foreach (var n in new[] { "A", "B", "C", "D", "E" }) plc.Names.Add(n);
        var reader = plc.Reader();
        var names = Defs("A", "B", "C", "D", "E");
        var r = reader.Read(names, new LookupBudget(2, TimeSpan.FromSeconds(1)));
        Assert.Equal(2, r.Count);
        r = reader.Read(names, new LookupBudget(2, TimeSpan.FromSeconds(1)));
        Assert.Equal(4, r.Count);
        r = reader.Read(names, new LookupBudget(2, TimeSpan.FromSeconds(1)));
        Assert.Equal(5, r.Count);
        Assert.Equal(5, plc.Lookups.Count);
    }

    [Fact]
    public void A_short_drop_still_shows_connected_and_is_written_to_history_once()
    {
        var t0 = new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
        var grace = new LinkGrace(TimeSpan.FromSeconds(5));
        Assert.Equal("disconnected", grace.Shown("disconnected", t0));   // never connected yet: no grace
        Assert.Null(grace.Good(t0));

        // Drop, back 2 s later: shown connected all along, one History event with the reason
        grace.Lost(t0.AddSeconds(1), "Reading the model tags failed: timed out");
        Assert.Equal("connected", grace.Shown("disconnected", t0.AddSeconds(1)));
        Assert.Equal("connected", grace.Shown("connecting", t0.AddSeconds(2)));
        var ev = grace.Good(t0.AddSeconds(3));
        Assert.NotNull(ev);
        Assert.Equal("link", ev!.Kind);
        Assert.Contains("2.0 s", ev.Text);
        Assert.Contains("timed out", ev.Text);
        Assert.Equal(1, grace.Drops);

        // A long drop: shown as lost once the grace (counted from the last good read) is over; MachineHistory.Diff
        // writes that, so there is no second event when it comes back
        Assert.Null(grace.Good(t0.AddSeconds(9)));
        grace.Lost(t0.AddSeconds(10), "PLC not answering");
        Assert.Equal("connected", grace.Shown("connecting", t0.AddSeconds(12)));
        Assert.Equal("disconnected", grace.Shown("disconnected", t0.AddSeconds(20)));
        Assert.Equal("connecting", grace.Shown("connecting", t0.AddSeconds(21)));   // stays honest until a good read
        Assert.Null(grace.Good(t0.AddSeconds(22)));
        Assert.Equal(2, grace.Drops);
        Assert.Equal("connected", grace.Shown("connected", t0.AddSeconds(23)));
    }

    [Fact]
    public void The_grace_window_is_short_and_grows_with_slow_polling()
    {
        var normal = LinkGrace.WindowFor(1000);
        Assert.InRange(normal.TotalSeconds, 2, 10);
        Assert.True(LinkGrace.WindowFor(5000) >= normal);
    }

    [Fact]
    public void Connection_count_follows_the_driver_and_spots_a_leaked_session()
    {
        var s = new SessionTracker();
        Assert.Equal(new SessionTracker.Change(0, false), s.On(S7CommPlusConnectionState.Disconnected, S7CommPlusConnectionState.Connecting));
        Assert.Equal(new SessionTracker.Change(1, false), s.On(S7CommPlusConnectionState.Connecting, S7CommPlusConnectionState.Connected));
        Assert.Equal(new SessionTracker.Change(0, false), s.On(S7CommPlusConnectionState.Connected, S7CommPlusConnectionState.Faulted));
        // The driver's silent reconnect from Faulted: the old connection is left open
        Assert.Equal(new SessionTracker.Change(0, true), s.On(S7CommPlusConnectionState.Faulted, S7CommPlusConnectionState.Connecting));
        Assert.Equal(new SessionTracker.Change(1, false), s.On(S7CommPlusConnectionState.Connecting, S7CommPlusConnectionState.Connected));
        Assert.Equal(new SessionTracker.Change(0, false), s.On(S7CommPlusConnectionState.Connected, S7CommPlusConnectionState.Disconnecting));
        Assert.Equal(new SessionTracker.Change(-1, false), s.On(S7CommPlusConnectionState.Disconnecting, S7CommPlusConnectionState.Disconnected));
        // Closing a faulted client the proper way is not a leak
        var t = new SessionTracker();
        t.On(S7CommPlusConnectionState.Connecting, S7CommPlusConnectionState.Connected);
        t.On(S7CommPlusConnectionState.Connected, S7CommPlusConnectionState.Faulted);
        t.On(S7CommPlusConnectionState.Faulted, S7CommPlusConnectionState.Disconnecting);
        Assert.Equal(new SessionTracker.Change(-1, false), t.On(S7CommPlusConnectionState.Disconnecting, S7CommPlusConnectionState.Disconnected));
    }
}
