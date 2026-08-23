using Microsoft.Extensions.DependencyInjection;
using NarrationStudio.Harness;
using NarrationStudio.Hosting;
using NarrationStudio.Services;
using NarrationStudio.Tools;

namespace NarrationStudio.Service;

public static class SessionStepRunner
{
    public static async Task RunAsync(
        ProductionJob job,
        string step,
        IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        var takeMutex = !SessionSteps.IsFanoutStep(step);
        Mutex? mutex = takeMutex ? new Mutex(false, "NarrationStudio.Production") : null;
        var owned = false;
        try
        {
            if (mutex is not null)
            {
                try
                {
                    owned = mutex.WaitOne(Timeout.Infinite);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<RuntimeJobOptions>();
            runtime.Studio = job.Studio;
            runtime.Model = job.Model;

            var pipeline = scope.ServiceProvider.GetRequiredService<PipelineStore>();
            if (!pipeline.AttachExisting(job.OutputDirectory))
            {
                throw new InvalidOperationException($"Session '{job.JobId}' has no pipeline-state.json. Create the session first.");
            }

            job.EventsPath = Path.Combine(pipeline.AgentMemoryDirectory, "events.jsonl");
            var tools = scope.ServiceProvider.GetRequiredService<ProductionTools>();
            var checks = scope.ServiceProvider.GetRequiredService<HarnessChecks>();
            job.MarkStepStarted(step, "running");
            var result = await SessionSteps.RunAsync(step, tools, checks);
            var snapshot = pipeline.Snapshot();
            job.MergedVideoPath = snapshot.MergedVideoPath;
            job.MarkStepSucceeded(step, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.MarkStepFailed(step, "Step cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            job.MarkStepFailed(step, ex is HarnessCheckException check ? check.Message : ex.Message);
            throw;
        }
        finally
        {
            if (owned && mutex is not null)
            {
                mutex.ReleaseMutex();
            }

            mutex?.Dispose();
        }
    }
}
