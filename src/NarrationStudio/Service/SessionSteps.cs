using NarrationStudio.Harness;
using NarrationStudio.Tools;

namespace NarrationStudio.Service;

public static class SessionSteps
{
    public const string Rewrite = "rewrite";
    public const string Segment = "segment";
    public const string PlanSlides = "plan-slides";
    public const string Render = "render";
    public const string Tts = "tts";
    public const string Compose = "compose";
    public const string Merge = "merge";
    public const string Verify = "verify";

    public static readonly IReadOnlyList<string> All =
    [
        Rewrite, Segment, PlanSlides, Render, Tts, Compose, Merge, Verify
    ];

    public static string? Normalize(string? step)
    {
        var value = (step ?? "").Trim().ToLowerInvariant();
        return All.Contains(value) ? value : null;
    }

    public static bool IsAsync(string step) =>
        step is PlanSlides or Tts;

    public static bool IsFanoutStep(string step) =>
        step is Render or Tts;

    public static bool AllowsConcurrent(IReadOnlyCollection<string> inFlight, string incoming)
    {
        if (inFlight.Contains(incoming))
        {
            return false;
        }

        return inFlight.Count == 1 && IsFanoutStep(inFlight.First()) && IsFanoutStep(incoming);
    }

    public static async Task<string> RunAsync(string step, ProductionTools tools, HarnessChecks checks)
    {
        return step switch
        {
            Rewrite => await tools.RephraseTranscriptIntoNarration(),
            Segment => tools.SegmentTranscriptIntoEightScripts(),
            PlanSlides => await tools.PlanSlidesForAllSegments(),
            Render => await tools.RenderAllSlideImages(),
            Tts => await tools.SynthesizeAllAudioAsync(),
            Compose => tools.ComposeAllSegmentVideos(),
            Merge => tools.MergeAllVideos(),
            Verify => RunVerify(checks),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown session step.")
        };
    }

    private static string RunVerify(HarnessChecks checks)
    {
        checks.Verify();
        return "Harness checks passed.";
    }
}
