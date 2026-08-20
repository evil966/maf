using System.Text.Json;
using Microsoft.Extensions.Options;
using NarrationStudio.Configuration;
using NarrationStudio.Models;

namespace NarrationStudio.Services;

public sealed class PipelineStore
{
    private readonly object _gate = new();
    private PipelineState _state;

    public PipelineStore(IOptions<StudioOptions> options)
    {
        Options = options.Value;
        _state = new PipelineState
        {
            RunId = DateTime.Now.ToString("yyyyMMdd_HHmmss"),
            StartedAt = DateTimeOffset.Now,
            Status = "created"
        };
    }

    public StudioOptions Options { get; }

    public string OutputDirectory => _state.OutputDirectory;

    public string ScriptsDirectory => Path.Combine(OutputDirectory, "scripts");
    public string SlidesDirectory => Path.Combine(OutputDirectory, "slides");
    public string AudioDirectory => Path.Combine(OutputDirectory, "audio");
    public string VideoDirectory => Path.Combine(OutputDirectory, "video");

    public PipelineState Snapshot()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(_state, PipelineJsonContext.Default.PipelineState);
            return JsonSerializer.Deserialize(json, PipelineJsonContext.Default.PipelineState)!;
        }
    }

    public PipelineState Mutate(Action<PipelineState> update)
    {
        lock (_gate)
        {
            update(_state);
            PersistUnlocked();
            return SnapshotUnlocked();
        }
    }

    public void Initialize(string transcriptPath, string outputDirectory)
    {
        lock (_gate)
        {
            _state.TranscriptPath = Path.GetFullPath(transcriptPath);
            _state.OutputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(_state.OutputDirectory);
            Directory.CreateDirectory(ScriptsDirectory);
            Directory.CreateDirectory(SlidesDirectory);
            Directory.CreateDirectory(AudioDirectory);
            Directory.CreateDirectory(VideoDirectory);
            PersistUnlocked();
        }
    }

    public string AudioFileName(int segmentNumber) => $"audio-{segmentNumber:00}.mp3";
    public string VideoFileName(int segmentNumber) => $"video-{segmentNumber:00}.mp4";
    public string MergedVideoFileName() => $"video_{_state.RunId}.mp4";

    public string AudioPath(int segmentNumber) => Path.Combine(AudioDirectory, AudioFileName(segmentNumber));
    public string VideoPath(int segmentNumber) => Path.Combine(VideoDirectory, VideoFileName(segmentNumber));
    public string MergedVideoPath() => Path.Combine(OutputDirectory, MergedVideoFileName());
    public string SlideImagePath(int segmentNumber, int slideNumber) =>
        Path.Combine(SlidesDirectory, $"segment-{segmentNumber:00}-slide-{slideNumber:00}.png");
    public string RewrittenNarrationPath() => Path.Combine(ScriptsDirectory, "narration-rewritten.txt");

    public string SummarizeForAgent()
    {
        var state = Snapshot();
        var rewritten = !string.IsNullOrWhiteSpace(state.NarrationPath) && File.Exists(state.NarrationPath);
        var scripts = state.Segments.Count(s => !string.IsNullOrWhiteSpace(s.Script));
        var slides = state.Segments.Count(s => s.Slides.Count > 0);
        var images = state.Segments.Sum(s => s.Slides.Count(sl => File.Exists(sl.ImagePath)));
        var audio = state.Segments.Count(s => !string.IsNullOrWhiteSpace(s.AudioPath) && File.Exists(s.AudioPath));
        var videos = state.Segments.Count(s => !string.IsNullOrWhiteSpace(s.VideoPath) && File.Exists(s.VideoPath));
        var merged = !string.IsNullOrWhiteSpace(state.MergedVideoPath) && File.Exists(state.MergedVideoPath);

        return
            $"""
            Pipeline status:
            - RunId: {state.RunId}
            - Transcript: {state.TranscriptPath}
            - Rewritten 7-minute narration: {(rewritten ? state.NarrationPath : "(none)")}
            - Output: {state.OutputDirectory}
            - Scripts saved: {scripts}/{Options.SegmentCount}
            - Segments with slide plans: {slides}/{Options.SegmentCount}
            - Slide images rendered: {images}
            - Audio files: {audio}/{Options.SegmentCount}
            - Segment videos: {videos}/{Options.SegmentCount}
            - Merged video ready: {merged}
            - Merged video path: {state.MergedVideoPath ?? "(none)"}
            Next required step:
            {(!rewritten ? "rephrase the raw transcript into a 7-minute executive talk via Ollama" :
              scripts < Options.SegmentCount ? "segment the rewritten narration into 8 scripts" :
              audio < Options.SegmentCount ? "synthesize remaining MP3 files" :
              slides < Options.SegmentCount ? "plan remaining slide phrases" :
              images < state.Segments.Sum(s => Math.Max(1, s.Slides.Count)) ? "render remaining slide images" :
              videos < Options.SegmentCount ? "compose remaining segment videos" :
              merged ? "done" : "merge all segment videos")}
            """;
    }

    private PipelineState SnapshotUnlocked()
    {
        var json = JsonSerializer.Serialize(_state, PipelineJsonContext.Default.PipelineState);
        return JsonSerializer.Deserialize(json, PipelineJsonContext.Default.PipelineState)!;
    }

    private void PersistUnlocked()
    {
        if (string.IsNullOrWhiteSpace(_state.OutputDirectory))
        {
            return;
        }

        var path = Path.Combine(_state.OutputDirectory, "pipeline-state.json");
        File.WriteAllText(path, JsonSerializer.Serialize(_state, PipelineJsonContext.Default.PipelineState));
    }
}
