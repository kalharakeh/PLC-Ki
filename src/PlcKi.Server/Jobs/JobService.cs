using System.Diagnostics;
using System.Text;

namespace PlcKi.Server.Jobs;

/// <summary>
/// One piece of long work started from the pages: export an archive, build a model, generate blocks.
/// It runs the same command as on the command line (PlcKi.Server export / import / develop) as a child process,
/// so the page and the command line behave the same, and a crash in the job cannot take the web app down.
/// </summary>
public sealed class Job
{
    private readonly StringBuilder _log = new();
    internal Process? Process;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string StartedBy { get; init; } = "";
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public DateTimeOffset? Ended { get; private set; }
    /// <summary>running, done (exit 0), problems (exit 1: finished but something is left), failed, stopped.</summary>
    public string State { get; private set; } = "running";
    public int? ExitCode { get; private set; }
    /// <summary>Last "##plcki stage" line: where the job is (generate, import v002, download, watch, fix 1, done).</summary>
    public string? Stage { get; private set; }
    /// <summary>The job waits for the person: "confirm-version v003" or "confirm-at-tia 120".</summary>
    public string? Waiting { get; private set; }
    public DateTimeOffset? WaitingSince { get; private set; }
    /// <summary>Files or names the job produced, for the page (model, export, package).</summary>
    public Dictionary<string, string> Outputs { get; init; } = new();
    internal Action<Job>? OnExit { get; init; }
    internal bool StopRequested;

    public int LogLength { get { lock (_log) return _log.Length; } }

    public string LogFrom(int from)
    {
        lock (_log) return from >= _log.Length ? "" : _log.ToString(Math.Max(0, from), _log.Length - Math.Max(0, from));
    }

    internal void Line(string line)
    {
        if (line.StartsWith("##plcki ", StringComparison.Ordinal))
        {
            var parts = line[8..].Split(' ', 2);
            switch (parts[0])
            {
                case "stage":
                    Stage = parts.Length > 1 ? parts[1] : null;
                    if (Waiting?.StartsWith("confirm-version") == true) { Waiting = null; WaitingSince = null; }
                    break;
                case "confirm-version":
                case "confirm-at-tia":
                    Waiting = line[8..];
                    WaitingSince = DateTimeOffset.Now;
                    break;
            }
            return;
        }
        lock (_log) _log.Append(line).Append('\n');
    }

    internal void Answered()
    {
        Waiting = null;
        WaitingSince = null;
    }

    internal void Finish(int code)
    {
        ExitCode = code;
        Ended = DateTimeOffset.Now;
        Waiting = null;
        State = StopRequested ? "stopped" : code == 0 ? "done" : code == 1 ? "problems" : "failed";
    }

    public object View(int from) => new
    {
        id = Id, kind = Kind, title = Title, startedBy = StartedBy, started = Started, ended = Ended,
        state = State, exitCode = ExitCode, stage = Stage, waiting = Waiting, waitingSince = WaitingSince,
        outputs = Outputs, log = LogFrom(from), logLength = LogLength,
    };
}

public sealed class JobException : Exception
{
    public JobException(string message, int status = 400) : base(message) => Status = status;
    public int Status { get; }
}

/// <summary>Runs one job at a time and keeps the last few for the pages.</summary>
public sealed class JobService : IDisposable
{
    private readonly List<Job> _jobs = new();
    private readonly ILogger<JobService> _log;

    public JobService(ILogger<JobService> log) => _log = log;

    public IReadOnlyList<Job> List()
    {
        lock (_jobs) return _jobs.OrderByDescending(j => j.Started).ToList();
    }

    public Job? Find(string id)
    {
        lock (_jobs) return _jobs.FirstOrDefault(j => j.Id == id);
    }

    public Job? Running
    {
        get { lock (_jobs) return _jobs.FirstOrDefault(j => j.State == "running"); }
    }

    /// <summary>Starts "PlcKi.Server &lt;args&gt;" as a child process. Only one job runs at a time.</summary>
    public Job Start(string kind, string title, string user, IEnumerable<string> args, IDictionary<string, string?>? env = null,
        Dictionary<string, string>? outputs = null, Action<Job>? onExit = null)
    {
        var job = new Job { Kind = kind, Title = title, StartedBy = user, Outputs = outputs ?? new(), OnExit = onExit };
        lock (_jobs)
        {
            if (Running is { } busy) throw new JobException($"Wait until \"{busy.Title}\" has finished, or stop it.", 409);
            _jobs.Add(job);
            foreach (var old in _jobs.Where(j => j.State != "running").OrderBy(j => j.Started).Take(Math.Max(0, _jobs.Count - 20)).ToList())
                _jobs.Remove(old);
        }

        var psi = SelfStart(args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.WorkingDirectory = AppContext.BaseDirectory;
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) job.Line(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) job.Line(e.Data); };
        p.Exited += (_, _) =>
        {
            try { p.WaitForExit(); } catch { /* already gone */ }
            job.Finish(p.ExitCode);
            _log.LogInformation("Job {Title} ({Id}) finished: {State}.", job.Title, job.Id, job.State);
            try { job.OnExit?.Invoke(job); } catch (Exception ex) { _log.LogWarning(ex, "After job {Id}", job.Id); }
        };
        job.Line("> " + title);
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            job.Line("Could not start: " + ex.Message);
            job.Finish(2);
            throw new JobException("Could not start the job: " + ex.Message, 500);
        }
        job.Process = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _log.LogInformation("Job {Title} ({Id}) started by {User}.", title, job.Id, user);
        return job;
    }

    /// <summary>Types an answer into the job (the confirmations of a real PLC download).</summary>
    public void Input(string id, string text)
    {
        var job = Find(id) ?? throw new JobException("No such job.", 404);
        if (job.State != "running" || job.Process == null) throw new JobException("The job has already finished.", 409);
        if (job.Waiting?.StartsWith("confirm-version") != true) throw new JobException("The job is not waiting for an answer.", 409);
        job.Line("(answered on the page: " + text.Trim() + ")");
        job.Process.StandardInput.WriteLine(text.Trim());
        job.Process.StandardInput.Flush();
        job.Answered();
    }

    public void Stop(string id)
    {
        var job = Find(id) ?? throw new JobException("No such job.", 404);
        if (job.State != "running" || job.Process == null) return;
        job.StopRequested = true;
        job.Line("Stopped from the page.");
        try { job.Process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    /// <summary>The start info for this same program with other arguments (exe, or "dotnet PlcKi.Server.dll").</summary>
    internal static ProcessStartInfo SelfStart(IEnumerable<string> args)
    {
        var exe = Environment.ProcessPath ?? "dotnet";
        var psi = new ProcessStartInfo(exe);
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "PlcKi.Server.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public void Dispose()
    {
        foreach (var j in List().Where(j => j.State == "running"))
            try { j.StopRequested = true; j.Process?.Kill(entireProcessTree: true); } catch { /* gone */ }
    }
}
