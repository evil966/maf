using NarrationStudio.Configuration;
using NarrationStudio.Services;

namespace NarrationStudio.Tests;

public sealed class TranscriptSegmenterTests
{
    [Fact]
    public void Segment_respects_configured_count()
    {
        var segmenter = new TranscriptSegmenter();
        var options = new StudioOptions { SegmentCount = 4 };
        var text = string.Join(" ", Enumerable.Range(1, 20).Select(i => $"Sentence number {i} is here."));

        var segments = segmenter.Segment(text, options);

        Assert.Equal(4, segments.Count);
        Assert.All(segments, s => Assert.False(string.IsNullOrWhiteSpace(s.Script)));
    }
}

public sealed class SlidePlannerTests
{
    [Fact]
    public void Plan_never_exceeds_max_slides()
    {
        var planner = new SlidePlannerService();
        var options = new StudioOptions { MaxSlidesPerSegment = 3 };
        var script = string.Join(" ", Enumerable.Range(1, 12).Select(i => $"This is sentence {i}."));

        var slides = planner.Plan(script, options);

        Assert.InRange(slides.Count, 1, 3);
        Assert.All(slides, s => Assert.False(string.IsNullOrWhiteSpace(s.SourcePhrase)));
    }
}

public sealed class StudioNamingTests
{
    [Fact]
    public void Audio_and_video_ranges_use_segment_count()
    {
        Assert.Equal("audio-01.mp3 ... audio-04.mp3", StudioNaming.AudioRange(4));
        Assert.Equal("video-01.mp4 ... video-08.mp4", StudioNaming.VideoRange(8));
    }
}
