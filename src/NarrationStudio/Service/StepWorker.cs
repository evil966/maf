using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NarrationStudio.Service;

public sealed class StepWorker : BackgroundService
{
    private readonly StepQueue _queue;
    private readonly JobStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StepWorker> _logger;

    public StepWorker(StepQueue queue, JobStore store, IServiceScopeFactory scopes, ILogger<StepWorker> logger)
    {
        _queue = queue;
        _store = store;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _queue.ReadAllAsync(stoppingToken))
        {
            var job = _store.Get(work.JobId);
            if (job is null)
            {
                continue;
            }

            try
            {
                await SessionStepRunner.RunAsync(job, work.Step, _scopes, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {JobId} step {Step} failed.", work.JobId, work.Step);
            }
        }
    }
}
