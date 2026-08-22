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
    public string AgentMemoryRoot => Path.Combine(OutputDirectory, "agent-file-memory");
    public string AgentMemoryDirectory => Path.Combine(AgentMemoryRoot, $"production-notes-{_state.RunId}");
    public string AgentMemoryWorkingFolder => Path.GetFileName(AgentMemoryDirectory);

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

    public int Initialize(string transcriptPath, string outputDirectory)
    {
        lock (_gate)
        {
            _state.TranscriptPath = Path.GetFullPath(transcriptPath);
            _state.OutputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(_state.OutputDirectory);
            var cleared = ClearGeneratedOutputsUnlocked();
            Directory.CreateDirectory(ScriptsDirectory);
            Directory.CreateDirectory(SlidesDirectory);
            Directory.CreateDirectory(AudioDirectory);
            Directory.CreateDirectory(VideoDirectory);
            Directory.CreateDirectory(AgentMemoryDirectory);
            PersistUnlocked();
            WriteRunHeaderUnlocked("starting");
            return cleared;
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
        lock (_gate)
        {
            return SummarizeUnlocked();
        }
    }

    private string SummarizeUnlocked()
    {
        var state = _state;
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
            - Agent file memory: {AgentMemoryDirectory}
            Next required step:
            {(!rewritten
                ? (Options.UseStubNarration
                    ? "copy voice_transcript_ollama.txt into the rewritten narration"
                    : "rephrase the raw transcript into a 7-minute executive talk via Ollama") :
              scripts < Options.SegmentCount ? $"segment the rewritten narration into {Options.SegmentCount} scripts" :
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
        WriteMemorySnapshotUnlocked();
    }

    public void WriteRunHeader(string orchestration, string ttsProvider, bool stubNarration, string? jobTitle = null)
    {
        lock (_gate)
        {
            WriteRunHeaderUnlocked(orchestration, ttsProvider, stubNarration, jobTitle);
        }
    }

    private void WriteRunHeaderUnlocked(string orchestration, string ttsProvider = "", bool? stubNarration = null, string? jobTitle = null)
    {
        if (string.IsNullOrWhiteSpace(_state.OutputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(AgentMemoryDirectory);
        var stub = stubNarration is null
            ? Options.UseStubNarration.ToString()
            : stubNarration.Value.ToString();
        File.WriteAllText(
            Path.Combine(AgentMemoryDirectory, "run.txt"),
            $"""
            RunId        : {_state.RunId}
            Title        : {(string.IsNullOrWhiteSpace(jobTitle) ? "(none)" : jobTitle)}
            StartedAt    : {_state.StartedAt:o}
            Orchestration: {orchestration}
            StubNarration: {stub}
            TTS          : {(string.IsNullOrWhiteSpace(ttsProvider) ? "(not recorded yet)" : ttsProvider)}
            Transcript   : {_state.TranscriptPath}
            Output       : {_state.OutputDirectory}
            MemoryFolder : {AgentMemoryDirectory}

            Direct mode does not start MAF FileMemoryProvider, so this folder is filled
            by Narration Studio snapshots (run.txt, pipeline-status.txt, notes.txt).
            Handoff/sequential may also write MAF files here.
            """);
        WriteMemorySnapshotUnlocked();
    }

    private void WriteMemorySnapshotUnlocked()
    {
        if (string.IsNullOrWhiteSpace(_state.OutputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(AgentMemoryDirectory);
        File.WriteAllText(Path.Combine(AgentMemoryDirectory, "pipeline-status.txt"), SummarizeUnlocked());
        File.WriteAllText(
            Path.Combine(AgentMemoryDirectory, "notes.txt"),
            _state.Notes.Count == 0
                ? "(no agent or tool notes yet)"
                : string.Join(Environment.NewLine + Environment.NewLine, _state.Notes));
    }

    private int ClearGeneratedOutputsUnlocked()
    {
        var deleted = 0;
        deleted += ClearDirectoryContents(ScriptsDirectory);
        deleted += ClearDirectoryContents(SlidesDirectory);
        deleted += ClearDirectoryContents(AudioDirectory);
        deleted += ClearDirectoryContents(VideoDirectory);

        foreach (var leftover in Directory.EnumerateFiles(_state.OutputDirectory, "video_*.mp4"))
        {
            if (DeleteWithRetry(leftover))
            {
                deleted++;
            }
        }

        if (DeleteWithRetry(Path.Combine(_state.OutputDirectory, "pipeline-state.json")))
        {
            deleted++;
        }

        return deleted;
    }

    private static int ClearDirectoryContents(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (DeleteWithRetry(file))
            {
                deleted++;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            try
            {
                Directory.Delete(child, recursive: true);
            }
            catch (IOException)
            {
                // files were already removed; leftover empty folders are harmless
            }
        }

        return deleted;
    }

    private static bool DeleteWithRetry(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        IOException? last = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (IOException ex) when (attempt < 8)
            {
                last = ex;
                Thread.Sleep(150 * attempt);
            }
            catch (UnauthorizedAccessException ex) when (attempt < 8)
            {
                last = new IOException(ex.Message, ex);
                Thread.Sleep(150 * attempt);
            }
        }

        throw new IOException($"Could not overwrite previous generated file: {path}. Close any player or editor using it and retry.", last);
    }
}
