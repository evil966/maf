using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NarrationStudio.Agents;
using NarrationStudio.Configuration;
using NarrationStudio.Orchestration;
using NarrationStudio.Services;
using NarrationStudio.Tools;

namespace NarrationStudio.Harness;

public sealed class ProductionHarness
{
    private readonly StudioOptions _options;
    private readonly PipelineStore _store;
    private readonly AgentFactory _agentFactory;
    private readonly WorkflowHost _workflowHost;
    private readonly DirectExecutor _direct;
    private readonly ProductionTools _tools;
    private readonly HarnessChecks _checks;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ProductionHarness> _logger;

    public ProductionHarness(
        IOptions<StudioOptions> options,
        PipelineStore store,
        AgentFactory agentFactory,
        WorkflowHost workflowHost,
        DirectExecutor direct,
        ProductionTools tools,
        HarnessChecks checks,
        IHostApplicationLifetime lifetime,
        ILogger<ProductionHarness> logger)
    {
        _options = options.Value;
        _store = store;
        _agentFactory = agentFactory;
        _workflowHost = workflowHost;
        _direct = direct;
        _tools = tools;
        _checks = checks;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task RunAsync(string orchestration, string transcriptPath)
    {
        var cancellation = _lifetime.ApplicationStopping;

        if (_options.UseStubNarration)
        {
            Console.WriteLine(await _tools.RephraseTranscriptIntoNarration());
        }

        if (orchestration is "direct" || !_agentFactory.TryCreate(out var crew) || crew is null)
        {
            await _direct.RunAsync(cancellation);
        }
        else
        {
            var workflow = orchestration == "sequential"
                ? _workflowHost.CreateSequentialWorkflow(crew)
                : _workflowHost.CreateHandoffWorkflow(crew);

            await _workflowHost.RunAsync(
                workflow,
                _workflowHost.BuildUserPrompt(transcriptPath),
                cancellation);

            if (string.IsNullOrWhiteSpace(_store.Snapshot().MergedVideoPath) ||
                !File.Exists(_store.Snapshot().MergedVideoPath))
            {
                _logger.LogWarning("Agents did not finish the merged video. Completing remaining steps with production tools.");
                await _direct.RunAsync(cancellation);
            }
        }

        _checks.Verify();
    }
}
