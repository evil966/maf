using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NarrationStudio.Harness;
using NarrationStudio.Hosting;
using NarrationStudio.Services;

namespace NarrationStudio.Service;

public sealed class JobWorker : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly JobStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<JobWorker> _logger;

    public JobWorker(
        JobQueue queue,
        JobStore store,
        IServiceScopeFactory scopes,
        IHttpClientFactory http,
        ILogger<JobWorker> logger)
    {
        _queue = queue;
        _store = store;
        _scopes = scopes;
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.ReadAllAsync(stoppingToken))
        {
            var job = _store.Get(jobId);
            if (job is null)
            {
                continue;
            }

            await RunJobAsync(job, stoppingToken);
        }
    }

    private async Task RunJobAsync(ProductionJob job, CancellationToken stoppingToken)
    {
        job.Status = "running";
        using var mutex = new Mutex(false, "NarrationStudio.Production");
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(Timeout.Infinite);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            using var scope = _scopes.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<RuntimeJobOptions>();
            runtime.Studio = job.Studio;
            runtime.Model = job.Model;

            var pipeline = scope.ServiceProvider.GetRequiredService<PipelineStore>();
            var harness = scope.ServiceProvider.GetRequiredService<ProductionHarness>();
            var tts = scope.ServiceProvider.GetRequiredService<TtsService>();

            pipeline.Initialize(job.TranscriptPath, job.OutputDirectory);
            pipeline.WriteRunHeader(job.Orchestration, tts.DescribeProvider(), job.Studio.UseStubNarration, job.Title);
            job.EventsPath = Path.Combine(pipeline.AgentMemoryDirectory, "events.jsonl");

            await harness.RunAsync(job.Orchestration, job.TranscriptPath);
            var snapshot = pipeline.Snapshot();
            job.MergedVideoPath = snapshot.MergedVideoPath;
            job.Status = "complete";
            job.CompletedAt = DateTimeOffset.Now;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            job.Status = "failed";
            job.Error = "Job cancelled.";
            job.CompletedAt = DateTimeOffset.Now;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed.", job.JobId);
            job.Status = "failed";
            job.Error = ex is HarnessCheckException check
                ? check.Message
                : ex.Message;
            job.CompletedAt = DateTimeOffset.Now;
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }

        await NotifyCallbackAsync(job, stoppingToken);
    }

    private async Task NotifyCallbackAsync(ProductionJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.CallbackUrl))
        {
            return;
        }

        try
        {
            var client = _http.CreateClient(nameof(JobWorker));
            using var response = await client.PostAsJsonAsync(
                job.CallbackUrl,
                JobEndpoints.ToStatus(job, publicBaseUrl: null),
                cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Callback for job {JobId} failed.", job.JobId);
        }
    }
}
