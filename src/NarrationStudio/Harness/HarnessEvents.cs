using System.Diagnostics;
using System.Text.Json;
using NarrationStudio.Services;

namespace NarrationStudio.Harness;

public sealed class HarnessEvents
{
    private readonly PipelineStore _store;
    private readonly object _gate = new();

    public HarnessEvents(PipelineStore store)
    {
        _store = store;
    }

    public string EventsPath => Path.Combine(_store.AgentMemoryDirectory, "events.jsonl");

    public void Write(string name, TimeSpan elapsed, bool ok, string? error)
    {
        if (string.IsNullOrWhiteSpace(_store.OutputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(_store.AgentMemoryDirectory);
        var line = JsonSerializer.Serialize(new HarnessEventRecord(
            DateTimeOffset.Now,
            name,
            Math.Round(elapsed.TotalMilliseconds, 1),
            ok,
            error));

        lock (_gate)
        {
            File.AppendAllText(EventsPath, line + Environment.NewLine);
        }
    }

    public T Trace<T>(string name, Func<T> work)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = work();
            Write(name, elapsed.Elapsed, true, null);
            return result;
        }
        catch (Exception ex)
        {
            Write(name, elapsed.Elapsed, false, ex.Message);
            throw;
        }
    }

    public async Task<T> TraceAsync<T>(string name, Func<Task<T>> work)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = await work();
            Write(name, elapsed.Elapsed, true, null);
            return result;
        }
        catch (Exception ex)
        {
            Write(name, elapsed.Elapsed, false, ex.Message);
            throw;
        }
    }

    private sealed record HarnessEventRecord(
        DateTimeOffset at,
        string name,
        double elapsedMs,
        bool ok,
        string? error);
}
