using System.Collections.Concurrent;

namespace NarrationStudio.Service;

public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, ProductionJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public ProductionJob Add(ProductionJob job)
    {
        if (!_jobs.TryAdd(job.JobId, job))
        {
            throw new InvalidOperationException($"Job '{job.JobId}' already exists.");
        }

        return job;
    }

    public ProductionJob? Get(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IReadOnlyCollection<ProductionJob> List() => _jobs.Values.ToArray();
}
