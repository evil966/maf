using NarrationStudio.Configuration;
using NarrationStudio.Models;
using NarrationStudio.Services;

namespace NarrationStudio.Harness;

public sealed class HarnessCheckException : InvalidOperationException
{
    public HarnessCheckException(IReadOnlyList<string> failures)
        : base("Harness checks failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures.Select(f => "- " + f)))
    {
        Failures = failures;
    }

    public IReadOnlyList<string> Failures { get; }
}

public sealed class HarnessChecks
{
    private readonly PipelineStore _store;

    public HarnessChecks(PipelineStore store)
    {
        _store = store;
    }

    public void Verify() => Verify(_store.Snapshot(), _store.Options);

    public static void Verify(PipelineState state, StudioOptions options)
    {
        var failures = new List<string>();
        var expected = options.SegmentCount;

        if (!string.Equals(state.Status, "complete", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"status is '{state.Status}', expected 'complete'");
        }

        if (state.Segments.Count != expected)
        {
            failures.Add($"script count is {state.Segments.Count}, expected {expected}");
        }

        foreach (var segment in state.Segments)
        {
            if (segment.Slides.Count > options.MaxSlidesPerSegment)
            {
                failures.Add($"segment {segment.Number:00} has {segment.Slides.Count} slides, max {options.MaxSlidesPerSegment}");
            }

            if (string.IsNullOrWhiteSpace(segment.AudioPath) || !File.Exists(segment.AudioPath))
            {
                failures.Add($"audio missing for segment {segment.Number:00}");
            }

            if (string.IsNullOrWhiteSpace(segment.VideoPath) || !File.Exists(segment.VideoPath))
            {
                failures.Add($"video missing for segment {segment.Number:00}");
            }
        }

        if (string.IsNullOrWhiteSpace(state.MergedVideoPath) || !File.Exists(state.MergedVideoPath))
        {
            failures.Add("merged video is missing");
        }

        if (failures.Count > 0)
        {
            throw new HarnessCheckException(failures);
        }
    }
}
