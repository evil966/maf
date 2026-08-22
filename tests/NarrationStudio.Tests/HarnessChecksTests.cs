using NarrationStudio.Configuration;
using NarrationStudio.Harness;
using NarrationStudio.Models;

namespace NarrationStudio.Tests;

public sealed class HarnessChecksTests
{
    [Fact]
    public void Verify_passes_when_artifacts_exist()
    {
        var root = Path.Combine(Path.GetTempPath(), "narration-studio-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new StudioOptions { SegmentCount = 2, MaxSlidesPerSegment = 2 };
            var state = new PipelineState
            {
                Status = "complete",
                MergedVideoPath = Path.Combine(root, "video_test.mp4"),
                Segments =
                [
                    CompleteSegment(root, 1),
                    CompleteSegment(root, 2)
                ]
            };
            File.WriteAllText(state.MergedVideoPath, "merged");

            HarnessChecks.Verify(state, options);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Verify_fails_when_merged_video_is_missing()
    {
        var options = new StudioOptions { SegmentCount = 1, MaxSlidesPerSegment = 2 };
        var state = new PipelineState
        {
            Status = "videos-ready",
            Segments = [new NarrationSegment { Number = 1, Script = "Talk." }]
        };

        var ex = Assert.Throws<HarnessCheckException>(() => HarnessChecks.Verify(state, options));
        Assert.Contains(ex.Failures, f => f.Contains("merged video", StringComparison.OrdinalIgnoreCase));
    }

    private static NarrationSegment CompleteSegment(string root, int number)
    {
        var audio = Path.Combine(root, $"audio-{number:00}.mp3");
        var video = Path.Combine(root, $"video-{number:00}.mp4");
        File.WriteAllText(audio, "a");
        File.WriteAllText(video, "v");
        return new NarrationSegment
        {
            Number = number,
            Script = $"Script {number}.",
            AudioPath = audio,
            VideoPath = video,
            Slides = [new SlideSpec { Number = 1, Phrase = "One sentence.", ImagePath = Path.Combine(root, $"s{number}.png") }]
        };
    }
}
