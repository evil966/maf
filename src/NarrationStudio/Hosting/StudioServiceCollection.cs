using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NarrationStudio.Agents;
using NarrationStudio.Configuration;
using NarrationStudio.Harness;
using NarrationStudio.Memory;
using NarrationStudio.Orchestration;
using NarrationStudio.Services;
using NarrationStudio.Tools;

namespace NarrationStudio.Hosting;

public static class StudioServiceCollection
{
    public static IServiceCollection AddNarrationStudioPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<RuntimeJobOptions>();
        services.AddScoped<IOptions<StudioOptions>>(sp =>
        {
            var runtime = sp.GetRequiredService<RuntimeJobOptions>();
            var value = runtime.Studio
                ?? configuration.GetSection(StudioOptions.SectionName).Get<StudioOptions>()
                ?? new StudioOptions();
            return Options.Create(value);
        });
        services.AddScoped<IOptions<OllamaOptions>>(sp =>
        {
            var runtime = sp.GetRequiredService<RuntimeJobOptions>();
            var value = runtime.Model
                ?? configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>()
                ?? new OllamaOptions();
            return Options.Create(value);
        });

        services.AddSingleton<TranscriptSegmenter>();
        services.AddSingleton<SlidePlannerService>();
        services.AddSingleton<SlideImageRenderer>();
        services.AddSingleton<VideoComposerService>();

        services.AddScoped<PipelineStore>();
        services.AddScoped<OllamaChatClientFactory>();
        services.AddScoped<NarrationRewriteService>();
        services.AddScoped<SlidePhraseRewriteService>();
        services.AddScoped<TtsService>();
        services.AddScoped<ProductionTools>();
        services.AddScoped<HarnessEvents>();
        services.AddScoped<HarnessChecks>();
        services.AddScoped<ProductionContextProvider>();
        services.AddScoped<AgentFactory>();
        services.AddScoped<WorkflowHost>();
        services.AddScoped<DirectExecutor>();
        services.AddScoped<ProductionHarness>();
        services.AddScoped<ProductionApp>();
        return services;
    }
}
