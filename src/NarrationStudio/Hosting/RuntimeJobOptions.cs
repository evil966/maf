using NarrationStudio.Configuration;

namespace NarrationStudio.Hosting;

public sealed class RuntimeJobOptions
{
    public StudioOptions? Studio { get; set; }
    public OllamaOptions? Model { get; set; }
}
