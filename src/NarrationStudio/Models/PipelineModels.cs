using System.Text.Json.Serialization;

namespace NarrationStudio.Models;

public sealed class PipelineState
{
    public string RunId { get; set; } = "";
    public string TranscriptPath { get; set; } = "";
    public string? NarrationPath { get; set; }
    public string OutputDirectory { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public string Status { get; set; } = "created";
    public List<NarrationSegment> Segments { get; set; } = [];
    public string? MergedVideoPath { get; set; }
    public List<string> Notes { get; set; } = [];
}

public sealed class NarrationSegment
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Script { get; set; } = "";
    public List<SlideSpec> Slides { get; set; } = [];
    public string? AudioPath { get; set; }
    public string? VideoPath { get; set; }
    public double? AudioDurationSeconds { get; set; }
}

public sealed class SlideSpec
{
    public int Number { get; set; }
    public string SourcePhrase { get; set; } = "";
    public string Phrase { get; set; } = "";
    public bool PhraseRewritten { get; set; }
    public string ImagePath { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PipelineState))]
[JsonSerializable(typeof(List<NarrationSegment>))]
internal partial class PipelineJsonContext : JsonSerializerContext;
