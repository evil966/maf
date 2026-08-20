using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NarrationStudio.Agents;
using NarrationStudio.Configuration;
using NarrationStudio.Orchestration;
using NarrationStudio.Services;

namespace NarrationStudio;

public sealed class ProductionApp
{
    private readonly StudioOptions _options;
    private readonly PipelineStore _store;
    private readonly AgentFactory _agentFactory;
    private readonly WorkflowHost _workflowHost;
    private readonly DirectPipelineRunner _direct;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ProductionApp> _logger;

    public ProductionApp(
        IOptions<StudioOptions> options,
        PipelineStore store,
        AgentFactory agentFactory,
        WorkflowHost workflowHost,
        DirectPipelineRunner direct,
        IHostApplicationLifetime lifetime,
        ILogger<ProductionApp> logger)
    {
        _options = options.Value;
        _store = store;
        _agentFactory = agentFactory;
        _workflowHost = workflowHost;
        _direct = direct;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task RunAsync(string[] args)
    {
        using var mutex = new Mutex(true, "NarrationStudio.Production", out var createdNew);
        if (!createdNew)
        {
            throw new InvalidOperationException("Narration Studio is already running. Stop the other instance and retry.");
        }

        var transcriptPath = ResolveTranscriptPath(args);
        var outputDirectory = ResolveOutputDirectory();
        var orchestration = ResolveOrchestration(args);

        _store.Initialize(transcriptPath, outputDirectory);

        Console.WriteLine("Narration Studio");
        Console.WriteLine($"Transcript : {transcriptPath}");
        Console.WriteLine($"Output     : {outputDirectory}");
        Console.WriteLine($"Mode       : {orchestration}");
        Console.WriteLine();

        try
        {
            if (orchestration is "direct" || !_agentFactory.TryCreate(out var crew) || crew is null)
            {
                await _direct.RunAsync();
            }
            else
            {
                var workflow = orchestration == "sequential"
                    ? _workflowHost.CreateSequentialWorkflow(crew)
                    : _workflowHost.CreateHandoffWorkflow(crew);

                await _workflowHost.RunAsync(
                    workflow,
                    _workflowHost.BuildUserPrompt(transcriptPath),
                    _lifetime.ApplicationStopping);

                if (string.IsNullOrWhiteSpace(_store.Snapshot().MergedVideoPath) ||
                    !File.Exists(_store.Snapshot().MergedVideoPath))
                {
                    _logger.LogWarning("Agents did not finish the merged video. Completing remaining steps with production tools.");
                    await _direct.RunAsync();
                }
            }

            var merged = _store.Snapshot().MergedVideoPath;
            Console.WriteLine();
            Console.WriteLine(merged is null ? "Pipeline finished without a merged video." : $"Done: {merged}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Production pipeline failed.");
            throw;
        }
    }

    private string ResolveTranscriptPath(string[] args)
    {
        var candidates = new List<string>();
        var fromArgs = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(fromArgs))
        {
            candidates.Add(fromArgs);
        }

        if (!string.IsNullOrWhiteSpace(_options.TranscriptPath))
        {
            candidates.Add(_options.TranscriptPath);
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "voice_transcript.txt"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "voice_transcript.txt"));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "voice_transcript.txt")));
        candidates.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "voice_transcript.txt")));

        var match = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);

        return match ?? throw new FileNotFoundException(
            "Could not find voice_transcript.txt. Pass the path as the first argument.");
    }

    private string ResolveOutputDirectory()
    {
        var configured = string.IsNullOrWhiteSpace(_options.OutputDirectory) ? "output" : _options.OutputDirectory;
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
    }

    private static string ResolveOrchestration(string[] args)
    {
        var option = args.FirstOrDefault(a => a.StartsWith("--orchestration=", StringComparison.OrdinalIgnoreCase));
        if (option is not null)
        {
            return option.Split('=', 2)[1].Trim().ToLowerInvariant();
        }

        var index = Array.FindIndex(args, a => a.Equals("--orchestration", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
        {
            return args[index + 1].Trim().ToLowerInvariant();
        }

        return "handoff";
    }
}
