using System.ComponentModel;
using System.Text.Json;
using NarrationStudio.Models;
using NarrationStudio.Services;

namespace NarrationStudio.Tools;

public sealed class ProductionTools
{
    private readonly PipelineStore _store;
    private readonly TranscriptSegmenter _segmenter;
    private readonly SlidePlannerService _slidePlanner;
    private readonly SlideImageRenderer _renderer;
    private readonly TtsService _tts;
    private readonly VideoComposerService _video;
    private readonly NarrationRewriteService _rewriter;

    public ProductionTools(
        PipelineStore store,
        TranscriptSegmenter segmenter,
        SlidePlannerService slidePlanner,
        SlideImageRenderer renderer,
        TtsService tts,
        VideoComposerService video,
        NarrationRewriteService rewriter)
    {
        _store = store;
        _segmenter = segmenter;
        _slidePlanner = slidePlanner;
        _renderer = renderer;
        _tts = tts;
        _video = video;
        _rewriter = rewriter;
    }

    [Description("Read the current pipeline status, including which scripts, slides, audio files, and videos already exist.")]
    public string GetPipelineStatus() => _store.SummarizeForAgent();

    [Description("Load the voice transcript file and return its text.")]
    public string LoadTranscript()
    {
        var state = _store.Snapshot();
        if (!File.Exists(state.TranscriptPath))
        {
            throw new FileNotFoundException("Transcript file was not found.", state.TranscriptPath);
        }

        return File.ReadAllText(state.TranscriptPath);
    }

    [Description("Load the source text that should be segmented. After rephrasing, this is the 7-minute Ollama narration, not the raw transcript.")]
    public string LoadNarrationSource()
    {
        var state = _store.Snapshot();
        if (!string.IsNullOrWhiteSpace(state.NarrationPath) && File.Exists(state.NarrationPath))
        {
            return File.ReadAllText(state.NarrationPath);
        }

        throw new InvalidOperationException("The 7-minute narration has not been written yet. Call RephraseTranscriptIntoNarration first.");
    }

    [Description("Submit voice_transcript.txt to the local Ollama model and rewrite it as a spoken 7-minute talk for a mixed audience leaning toward executives. Saves output/scripts/narration-rewritten.txt. Later steps must use that file.")]
    public async Task<string> RephraseTranscriptIntoNarration()
    {
        var transcript = LoadTranscript();
        var narration = await _rewriter.RephraseAsync(transcript, CancellationToken.None);
        var path = _store.RewrittenNarrationPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, narration);
        _store.Mutate(state =>
        {
            state.NarrationPath = path;
            state.Status = "narration-ready";
            state.Notes.Add($"Rewrote transcript into a {CountWords(narration)}-word talk.");
        });

        return $"Saved {CountWords(narration)}-word narration to {path}.";
    }

    [Description("Split the rewritten 7-minute narration into exactly eight narration scripts and save them under output/scripts.")]
    public string SegmentTranscriptIntoEightScripts()
    {
        var narration = LoadNarrationSource();
        var segments = _segmenter.Segment(narration, _store.Options);
        if (segments.Count != _store.Options.SegmentCount)
        {
            throw new InvalidOperationException($"Expected {_store.Options.SegmentCount} scripts, got {segments.Count}.");
        }

        _segmenter.WriteScriptFiles(segments, _store);
        _store.Mutate(state =>
        {
            state.Segments = segments;
            state.Status = "scripts-ready";
            state.Notes.Add($"Created {segments.Count} narration scripts from the rewritten talk.");
        });

        return $"Saved {segments.Count} narration scripts to {_store.ScriptsDirectory}.";
    }

    [Description("Plan at most five image-slide phrases for every narration segment.")]
    public string PlanSlidesForAllSegments()
    {
        EnsureScripts();
        _store.Mutate(state =>
        {
            foreach (var segment in state.Segments)
            {
                segment.Slides = _slidePlanner.Plan(segment.Script, _store.Options);
            }

            state.Status = "slides-planned";
        });

        var snapshot = _store.Snapshot();
        return $"Planned slides for {snapshot.Segments.Count} segments. Max per segment: {_store.Options.MaxSlidesPerSegment}.";
    }

    [Description("Render PNG slides for every planned phrase. Each slide contains the phrase text for that part of the narration.")]
    public string RenderAllSlideImages()
    {
        EnsureScripts();
        var snapshot = _store.Snapshot();
        foreach (var segment in snapshot.Segments)
        {
            if (segment.Slides.Count == 0)
            {
                segment.Slides = _slidePlanner.Plan(segment.Script, _store.Options);
            }

            if (segment.Slides.Count > _store.Options.MaxSlidesPerSegment)
            {
                segment.Slides = segment.Slides.Take(_store.Options.MaxSlidesPerSegment).ToList();
            }

            foreach (var slide in segment.Slides)
            {
                var path = _store.SlideImagePath(segment.Number, slide.Number);
                _renderer.Render(segment, slide, _store.Options, path);
                slide.ImagePath = path;
            }
        }

        _store.Mutate(state =>
        {
            state.Segments = snapshot.Segments;
            state.Status = "slides-rendered";
        });

        return $"Rendered slide images in {_store.SlidesDirectory}.";
    }

    [Description("Create audio-01.mp3 through audio-08.mp3 from the eight narration scripts using ElevenLabs when configured, otherwise Windows speech.")]
    public async Task<string> SynthesizeAllAudioAsync()
    {
        EnsureScripts();
        var state = _store.Snapshot();
        foreach (var segment in state.Segments)
        {
            var path = _store.AudioPath(segment.Number);
            await _tts.SynthesizeAsync(segment.Script, path, CancellationToken.None);

            var duration = TtsService.ProbeDurationSeconds(path);
            _store.Mutate(current =>
            {
                var match = current.Segments.First(s => s.Number == segment.Number);
                match.AudioPath = path;
                match.AudioDurationSeconds = duration;
            });
        }

        _store.Mutate(current => current.Status = "audio-ready");
        return $"Wrote MP3 files to {_store.AudioDirectory}.";
    }

    [Description("Create video-01.mp4 through video-08.mp4, syncing each segment's slides to that segment's MP3.")]
    public string ComposeAllSegmentVideos()
    {
        var state = _store.Snapshot();
        foreach (var segment in state.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.AudioPath) || segment.Slides.All(s => string.IsNullOrWhiteSpace(s.ImagePath)))
            {
                throw new InvalidOperationException("Audio and slides must exist before composing videos.");
            }

            var path = _store.VideoPath(segment.Number);
            _video.ComposeSegment(segment, _store.Options, path);

            _store.Mutate(current => current.Segments.First(s => s.Number == segment.Number).VideoPath = path);
        }

        _store.Mutate(current => current.Status = "videos-ready");
        return $"Wrote segment videos to {_store.VideoDirectory}.";
    }

    [Description("Merge video-01.mp4 through video-08.mp4 into video_yyyyMMdd_HHmmss.mp4.")]
    public string MergeAllVideos()
    {
        var state = _store.Snapshot();
        var videos = state.Segments
            .OrderBy(s => s.Number)
            .Select(s => s.VideoPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .ToList();

        if (videos.Count != _store.Options.SegmentCount)
        {
            throw new InvalidOperationException($"Need {_store.Options.SegmentCount} segment videos before merging. Found {videos.Count}.");
        }

        var merged = _store.MergedVideoPath();
        _video.Merge(videos, merged);
        _store.Mutate(current =>
        {
            current.MergedVideoPath = merged;
            current.Status = "complete";
        });

        return $"Merged video: {merged}";
    }

    [Description("Save narration scripts provided as JSON. The JSON must be an array of objects with number, title, and script. Exactly eight items are required.")]
    public string SaveNarrationScripts(string segmentsJson)
    {
        var segments = JsonSerializer.Deserialize(segmentsJson, PipelineJsonContext.Default.ListNarrationSegment)
            ?? throw new InvalidOperationException("Could not parse narration scripts JSON.");

        if (segments.Count != _store.Options.SegmentCount)
        {
            throw new InvalidOperationException($"Exactly {_store.Options.SegmentCount} scripts are required.");
        }

        foreach (var segment in segments.OrderBy(s => s.Number).Select((s, i) => (s, i)))
        {
            segment.s.Number = segment.i + 1;
        }

        _segmenter.WriteScriptFiles(segments, _store);
        _store.Mutate(state =>
        {
            state.Segments = segments;
            state.Status = "scripts-ready";
        });

        return $"Saved {segments.Count} agent-authored narration scripts.";
    }

    private void EnsureScripts()
    {
        if (_store.Snapshot().Segments.Count != _store.Options.SegmentCount)
        {
            throw new InvalidOperationException("Narration scripts have not been created yet.");
        }
    }

    private static int CountWords(string text) =>
        text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
}
