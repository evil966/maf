using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NarrationStudio.Configuration;
using NarrationStudio.Harness;
using NarrationStudio.Services;

namespace NarrationStudio;

public sealed class ProductionApp
{
    private readonly StudioOptions _options;
    private readonly PipelineStore _store;
    private readonly ProductionHarness _harness;
    private readonly TtsService _tts;
    private readonly ILogger<ProductionApp> _logger;

    public ProductionApp(
        IOptions<StudioOptions> options,
        PipelineStore store,
        ProductionHarness harness,
        TtsService tts,
        ILogger<ProductionApp> logger)
    {
        _options = options.Value;
        _store = store;
        _harness = harness;
        _tts = tts;
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

        var cleared = _store.Initialize(transcriptPath, outputDirectory);

        Console.WriteLine("Narration Studio");
        Console.WriteLine($"Transcript : {transcriptPath}");
        Console.WriteLine($"Output     : {outputDirectory}");
        Console.WriteLine($"Mode       : {orchestration}");
        Console.WriteLine("Harness    : MAF+tools");
        Console.WriteLine(cleared == 0
            ? "Previous  : none (clean output folder)"
            : $"Previous  : cleared {cleared} generated files");
        if (_options.UseStubNarration)
        {
            Console.WriteLine($"Rewrite    : stub ({_options.StubNarrationPath})");
        }

        if (_options.UseStubSlidePhrases)
        {
            Console.WriteLine("Slides     : stub phrases (no Ollama rewrite)");
        }

        Console.WriteLine($"TTS        : {_tts.DescribeProvider()}");
        Console.WriteLine($"Memory     : {_store.AgentMemoryDirectory}");
        _store.WriteRunHeader(orchestration, _tts.DescribeProvider(), _options.UseStubNarration);

        Console.WriteLine();

        try
        {
            await _harness.RunAsync(orchestration, transcriptPath);

            var merged = _store.Snapshot().MergedVideoPath;
            Console.WriteLine();
            Console.WriteLine(merged is null ? "Pipeline finished without a merged video." : $"Done: {merged}");
        }
        catch (HarnessCheckException ex)
        {
            _logger.LogError(ex, "Harness checks failed after the production run.");
            Console.WriteLine();
            Console.WriteLine(ex.Message);
            throw;
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
