namespace NarrationStudio.Configuration;

public sealed class OllamaOptions
{
    public const string SectionName = "Model";

    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3.8:latest";
    public int TimeoutMinutes { get; set; } = 30;
}
