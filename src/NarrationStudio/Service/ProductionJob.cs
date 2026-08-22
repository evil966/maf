using NarrationStudio.Configuration;

namespace NarrationStudio.Service;

public sealed class ProductionJob
{
    public string JobId { get; init; } = "";
    public string? Title { get; init; }
    public string Orchestration { get; init; } = "direct";
    public string? CallbackUrl { get; init; }
    public bool KeepArtifacts { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public StudioOptions Studio { get; init; } = new();
    public OllamaOptions Model { get; init; } = new();
    public string OutputDirectory { get; init; } = "";
    public string TranscriptPath { get; init; } = "";

    public string Status { get; set; } = "queued";
    public string? Error { get; set; }
    public string? MergedVideoPath { get; set; }
    public string? EventsPath { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
