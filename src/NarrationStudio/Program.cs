using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NarrationStudio;
using NarrationStudio.Configuration;
using NarrationStudio.Hosting;
using NarrationStudio.Service;

var isCli = IsProductionCli(args);

if (isCli)
{
    var builder = Host.CreateApplicationBuilder(args);
    AddJsonConfig(builder.Configuration);
    builder.Services.AddNarrationStudioPipeline(builder.Configuration);
    using var host = builder.Build();
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ProductionApp>().RunAsync(args);
    return;
}

var web = WebApplication.CreateBuilder(args);
AddJsonConfig(web.Configuration);
web.Services.Configure<ServiceOptions>(web.Configuration.GetSection(ServiceOptions.SectionName));
web.Services.AddNarrationStudioPipeline(web.Configuration);
web.Services.AddSingleton<JobStore>();
web.Services.AddSingleton<JobQueue>();
web.Services.AddSingleton<StepQueue>();
web.Services.AddHttpClient(nameof(JobWorker));
web.Services.AddHostedService<JobWorker>();
web.Services.AddHostedService<StepWorker>();

var listenUrl = web.Configuration[$"{ServiceOptions.SectionName}:ListenUrl"];
if (string.IsNullOrWhiteSpace(listenUrl))
{
    listenUrl = "http://0.0.0.0:5088";
}

web.WebHost.UseUrls(listenUrl);

var app = web.Build();
var serviceOptions = app.Services.GetRequiredService<IOptions<ServiceOptions>>().Value;
if (string.IsNullOrWhiteSpace(serviceOptions.ApiKey))
{
    app.Logger.LogWarning("Service:ApiKey is empty. The job API accepts unauthenticated requests.");
}

app.UseMiddleware<ApiKeyMiddleware>();
app.MapJobEndpoints();
app.Logger.LogInformation("Narration Studio service listening on {Url}", listenUrl);
await app.RunAsync();

static void AddJsonConfig(ConfigurationManager configuration)
{
    configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
    configuration.AddJsonFile(
        Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
        optional: true,
        reloadOnChange: true);
}

static bool IsProductionCli(string[] args)
{
    if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    if (args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase)))
    {
        return true;
    }

    if (args.Any(a => a.StartsWith("--orchestration", StringComparison.OrdinalIgnoreCase)))
    {
        return true;
    }

    return args.Any(a => !a.StartsWith("--", StringComparison.Ordinal));
}
