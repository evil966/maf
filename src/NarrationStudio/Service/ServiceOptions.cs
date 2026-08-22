namespace NarrationStudio.Service;

public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    public string ListenUrl { get; set; } = "http://0.0.0.0:5088";
    public string ApiKey { get; set; } = "";
}
