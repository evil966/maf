using NarrationStudio.Configuration;
using NarrationStudio.Service;

namespace NarrationStudio.Tests;

public sealed class JobOptionsMergerTests
{
    [Fact]
    public void Apply_overlays_request_onto_defaults_without_mutating_source()
    {
        var studio = new StudioOptions
        {
            SegmentCount = 8,
            MaxSlidesPerSegment = 5,
            TargetTalkMinutes = 7,
            UseStubNarration = true,
            ElevenLabsVoiceId = "default-voice"
        };
        var ollama = new OllamaOptions { Endpoint = "http://localhost:11434", Model = "qwen3.8:latest", TimeoutMinutes = 30 };
        var request = new JobRequest
        {
            SegmentCount = 4,
            UseStubNarration = false,
            Endpoint = "http://127.0.0.1:11434",
            RewriteExtraInstructions = "Name the product Atlas."
        };

        var (mergedStudio, mergedOllama) = JobOptionsMerger.Apply(studio, ollama, request);

        Assert.Equal(4, mergedStudio.SegmentCount);
        Assert.False(mergedStudio.UseStubNarration);
        Assert.Equal("Name the product Atlas.", mergedStudio.RewriteExtraInstructions);
        Assert.Equal("http://127.0.0.1:11434", mergedOllama.Endpoint);
        Assert.Equal(8, studio.SegmentCount);
        Assert.True(studio.UseStubNarration);
        Assert.Equal("http://localhost:11434", ollama.Endpoint);
        Assert.Equal("default-voice", mergedStudio.ElevenLabsVoiceId);
    }

    [Theory]
    [InlineData(null, "direct")]
    [InlineData("handoff", "handoff")]
    [InlineData("SEQUENTIAL", "sequential")]
    [InlineData("typo", "direct")]
    public void NormalizeOrchestration_defaults_unknown_values_to_direct(string? input, string expected)
    {
        Assert.Equal(expected, JobOptionsMerger.NormalizeOrchestration(input));
    }
}

public sealed class JobRequestJsonTests
{
    [Fact]
    public void Reads_string_booleans_and_numbers_from_json()
    {
        const string json = """{"useStubNarration":"true","useStubSlidePhrases":"false","segmentCount":"4","keepArtifacts":"true"}""";
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var request = System.Text.Json.JsonSerializer.Deserialize<JobRequest>(json, options);

        Assert.NotNull(request);
        Assert.True(request.UseStubNarration);
        Assert.False(request.UseStubSlidePhrases);
        Assert.Equal(4, request.SegmentCount);
        Assert.True(request.KeepArtifacts);
    }
}

public sealed class JobPathsTests
{
    [Fact]
    public void Job_folders_are_isolated_under_output_jobs()
    {
        var root = JobPaths.ResolveOutputRoot("output", @"E:\exercises\maf");
        var job = JobPaths.JobDirectory(root, "abc");

        Assert.Equal(Path.Combine(@"E:\exercises\maf", "output"), root);
        Assert.Equal(Path.Combine(root, "jobs", "abc"), job);
        Assert.Equal(Path.Combine(job, "inputs", "stub-narration.txt"), JobPaths.StubNarrationFile(job));
        Assert.Equal(Path.Combine(job, "inputs", "transcript.txt"), JobPaths.TranscriptFile(job));
    }
}
