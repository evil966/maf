using Microsoft.Extensions.Logging;
using NarrationStudio.Services;
using NarrationStudio.Tools;

namespace NarrationStudio.Orchestration;

public sealed class DirectPipelineRunner
{
    private readonly PipelineStore _store;
    private readonly ProductionTools _tools;
    private readonly ILogger<DirectPipelineRunner> _logger;

    public DirectPipelineRunner(PipelineStore store, ProductionTools tools, ILogger<DirectPipelineRunner> logger)
    {
        _store = store;
        _tools = tools;
        _logger = logger;
    }

    public async Task<string> RunAsync()
    {
        _logger.LogInformation("Running deterministic production tools.");
        Console.WriteLine(_tools.GetPipelineStatus());

        var state = _store.Snapshot();
        if (string.IsNullOrWhiteSpace(state.NarrationPath) || !File.Exists(state.NarrationPath))
        {
            Console.WriteLine(await _tools.RephraseTranscriptIntoNarration());
        }

        if (_store.Snapshot().Segments.Count != _store.Options.SegmentCount)
        {
            Console.WriteLine(_tools.SegmentTranscriptIntoEightScripts());
        }

        Console.WriteLine(_tools.PlanSlidesForAllSegments());
        Console.WriteLine(_tools.RenderAllSlideImages());
        Console.WriteLine(await _tools.SynthesizeAllAudioAsync());
        Console.WriteLine(_tools.ComposeAllSegmentVideos());
        var merged = _tools.MergeAllVideos();
        Console.WriteLine(merged);
        return merged;
    }
}
