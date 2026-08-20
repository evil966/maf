using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NarrationStudio;
using NarrationStudio.Agents;
using NarrationStudio.Configuration;
using NarrationStudio.Memory;
using NarrationStudio.Orchestration;
using NarrationStudio.Services;
using NarrationStudio.Tools;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: true);

builder.Services.Configure<StudioOptions>(builder.Configuration.GetSection(StudioOptions.SectionName));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.AddSingleton<PipelineStore>();
builder.Services.AddSingleton<OllamaChatClientFactory>();
builder.Services.AddSingleton<NarrationRewriteService>();
builder.Services.AddSingleton<TranscriptSegmenter>();
builder.Services.AddSingleton<SlidePlannerService>();
builder.Services.AddSingleton<SlideImageRenderer>();
builder.Services.AddSingleton<TtsService>();
builder.Services.AddSingleton<VideoComposerService>();
builder.Services.AddSingleton<ProductionTools>();
builder.Services.AddSingleton<ProductionContextProvider>();
builder.Services.AddSingleton<AgentFactory>();
builder.Services.AddSingleton<WorkflowHost>();
builder.Services.AddSingleton<DirectPipelineRunner>();
builder.Services.AddSingleton<ProductionApp>();

using var host = builder.Build();
await host.Services.GetRequiredService<ProductionApp>().RunAsync(args);
