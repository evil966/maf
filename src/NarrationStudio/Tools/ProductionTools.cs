using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NarrationStudio.Configuration;
using NarrationStudio.Harness;
using NarrationStudio.Models;
using NarrationStudio.Service;
using NarrationStudio.Services;

namespace NarrationStudio.Tools;

public sealed class ProductionTools
{
    private readonly PipelineStore _store;
    private readonly TranscriptSegmenter _segmenter;
    private readonly SlidePlannerService _slidePlanner;
    private readonly SlidePhraseRewriteService _slidePhrases;
    private readonly SlideImageRenderer _renderer;
    private readonly TtsService _tts;
    private readonly VideoComposerService _video;
    private readonly NarrationRewriteService _rewriter;
    private readonly HarnessEvents _events;
    private readonly CancellationToken _cancellation;

    public ProductionTools(
        PipelineStore store,
        TranscriptSegmenter segmenter,
        SlidePlannerService slidePlanner,
        SlidePhraseRewriteService slidePhrases,
        SlideImageRenderer renderer,
        TtsService tts,
        VideoComposerService video,
        NarrationRewriteService rewriter,
        HarnessEvents events,
        IHostApplicationLifetime lifetime)
    {
        _store = store;
        _segmenter = segmenter;
        _slidePlanner = slidePlanner;
        _slidePhrases = slidePhrases;
        _renderer = renderer;
        _tts = tts;
        _video = video;
        _rewriter = rewriter;
        _events = events;
        _cancellation = lifetime.ApplicationStopping;
    }

    [Description("Read the current pipeline status, including which scripts, slides, audio files, and videos already exist.")]
    public string GetPipelineStatus() => _events.Trace(nameof(GetPipelineStatus), () => _store.SummarizeForAgent());

    [Description("Load the voice transcript file and return its text.")]
    public string LoadTranscript()
    {
        return _events.Trace(nameof(LoadTranscript), () =>
        {
            var state = _store.Snapshot();
            if (!File.Exists(state.TranscriptPath))
            {
                throw new FileNotFoundException("Transcript file was not found.", state.TranscriptPath);
            }

            return File.ReadAllText(state.TranscriptPath);
        });
    }

    [Description("Load the rewritten spoken narration that should be segmented, not the raw transcript.")]
    public string LoadNarrationSource()
    {
        return _events.Trace(nameof(LoadNarrationSource), () =>
        {
            var state = _store.Snapshot();
            if (!string.IsNullOrWhiteSpace(state.NarrationPath) && File.Exists(state.NarrationPath))
            {
                return File.ReadAllText(state.NarrationPath);
            }

            throw new InvalidOperationException("The rewritten narration has not been written yet. Call RephraseTranscriptIntoNarration first.");
        });
    }

    [Description("Rewrite the raw transcript into a spoken talk of Studio:TargetTalkMinutes and save narration-rewritten.txt. Later steps must use that file.")]
    public Task<string> RephraseTranscriptIntoNarration()
    {
        return _events.TraceAsync(nameof(RephraseTranscriptIntoNarration), async () =>
        {
            var transcript = LoadTranscriptCore();
            var narration = await _rewriter.RephraseAsync(transcript, _cancellation);
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
        });
    }

    [Description("Split the rewritten narration into Studio:SegmentCount scripts and save them under output/scripts.")]
    public string SegmentTranscriptIntoEightScripts()
    {
        return _events.Trace(nameof(SegmentTranscriptIntoEightScripts), () =>
        {
            var narration = LoadNarrationSourceCore();
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
        });
    }

    [Description("Plan at most Studio:MaxSlidesPerSegment phrases per segment, then rewrite each as one on-screen sentence.")]
    public Task<string> PlanSlidesForAllSegments()
    {
        return _events.TraceAsync(nameof(PlanSlidesForAllSegments), async () =>
        {
            EnsureScripts();
            var snapshot = _store.Snapshot();
            foreach (var segment in snapshot.Segments)
            {
                segment.Slides = _slidePlanner.Plan(segment.Script, _store.Options);
            }

            await _slidePhrases.RewriteAllAsync(snapshot.Segments, _cancellation);
            _store.Mutate(state =>
            {
                state.Segments = snapshot.Segments;
                state.Status = "slides-planned";
            });

            var slides = snapshot.Segments.Sum(s => s.Slides.Count);
            return $"Planned {slides} slides. Max per segment: {_store.Options.MaxSlidesPerSegment}.";
        });
    }

    [Description("Render PNG slides for every planned phrase. Each slide shows the on-screen sentence, not the raw excerpt.")]
    public Task<string> RenderAllSlideImages()
    {
        return _events.TraceAsync(nameof(RenderAllSlideImages), async () =>
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
            }

            await _slidePhrases.RewriteAllAsync(snapshot.Segments, _cancellation);

            foreach (var segment in snapshot.Segments)
            {
                foreach (var slide in segment.Slides)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    var path = _store.SlideImagePath(segment.Number, slide.Number);
                    _renderer.Render(segment, slide, _store.Options, path);
                    slide.ImagePath = path;
                }
            }

            _store.Mutate(state => PipelineSegmentPatch.ApplySlides(state, snapshot.Segments));

            return $"Rendered slide images in {_store.SlidesDirectory}.";
        });
    }

    [Description("Synthesize one MP3 per narration script using ElevenLabs when configured, otherwise Windows speech.")]
    public Task<string> SynthesizeAllAudioAsync()
    {
        return _events.TraceAsync(nameof(SynthesizeAllAudioAsync), async () =>
        {
            EnsureScripts();
            var state = _store.Snapshot();
            foreach (var segment in state.Segments)
            {
                var path = _store.AudioPath(segment.Number);
                await _tts.SynthesizeAsync(segment.Script, path, _cancellation);

                var duration = TtsService.ProbeDurationSeconds(path, _cancellation);
                _store.Mutate(current =>
                {
                    var match = current.Segments.First(s => s.Number == segment.Number);
                    match.AudioPath = path;
                    match.AudioDurationSeconds = duration;
                });
            }

            _store.Mutate(current => current.Status = "audio-ready");
            return $"Wrote MP3 files to {_store.AudioDirectory}.";
        });
    }

    [Description("Compose one segment MP4 per script, syncing that segment's slides to its MP3.")]
    public string ComposeAllSegmentVideos()
    {
        return _events.Trace(nameof(ComposeAllSegmentVideos), () =>
        {
            var state = _store.Snapshot();
            foreach (var segment in state.Segments)
            {
                if (string.IsNullOrWhiteSpace(segment.AudioPath) || segment.Slides.All(s => string.IsNullOrWhiteSpace(s.ImagePath)))
                {
                    throw new InvalidOperationException("Audio and slides must exist before composing videos.");
                }

                var path = _store.VideoPath(segment.Number);
                _video.ComposeSegment(segment, _store.Options, path, _cancellation);

                _store.Mutate(current => current.Segments.First(s => s.Number == segment.Number).VideoPath = path);
            }

            _store.Mutate(current => current.Status = "videos-ready");
            return $"Wrote segment videos to {_store.VideoDirectory}.";
        });
    }

    [Description("Merge the segment MP4s into video_yyyyMMdd_HHmmss.mp4.")]
    public string MergeAllVideos()
    {
        return _events.Trace(nameof(MergeAllVideos), () =>
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
            _video.Merge(videos, merged, _cancellation);
            _store.Mutate(current =>
            {
                current.MergedVideoPath = merged;
                current.Status = "complete";
            });

            return $"Merged video: {merged}";
        });
    }

    [Description("Save narration scripts provided as JSON. The JSON must be an array of objects with number, title, and script. Count must equal Studio:SegmentCount.")]
    public string SaveNarrationScripts(string segmentsJson)
    {
        return _events.Trace(nameof(SaveNarrationScripts), () =>
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
        });
    }

    private string LoadTranscriptCore()
    {
        var state = _store.Snapshot();
        if (!File.Exists(state.TranscriptPath))
        {
            throw new FileNotFoundException("Transcript file was not found.", state.TranscriptPath);
        }

        return File.ReadAllText(state.TranscriptPath);
    }

    private string LoadNarrationSourceCore()
    {
        var state = _store.Snapshot();
        if (!string.IsNullOrWhiteSpace(state.NarrationPath) && File.Exists(state.NarrationPath))
        {
            return File.ReadAllText(state.NarrationPath);
        }

        throw new InvalidOperationException("The rewritten narration has not been written yet. Call RephraseTranscriptIntoNarration first.");
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
