using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NarrationStudio.Configuration;

namespace NarrationStudio.Service;

public static class JobEndpoints
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/v1/jobs", CreateJobAsync);
        app.MapGet("/v1/jobs/{jobId}", GetJob);
        app.MapGet("/v1/jobs/{jobId}/events", GetEvents);
        app.MapGet("/v1/jobs/{jobId}/artifacts/merged", DownloadMerged);
    }

    public static JobStatusResponse ToStatus(ProductionJob job, string? publicBaseUrl)
    {
        string? url = null;
        if (!string.IsNullOrWhiteSpace(publicBaseUrl) &&
            string.Equals(job.Status, "complete", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(job.MergedVideoPath) &&
            File.Exists(job.MergedVideoPath))
        {
            url = $"{publicBaseUrl.TrimEnd('/')}/v1/jobs/{job.JobId}/artifacts/merged";
        }

        return new JobStatusResponse(
            job.JobId,
            job.Status,
            job.Title,
            job.Orchestration,
            job.Error,
            job.MergedVideoPath,
            url,
            job.CreatedAt,
            job.CompletedAt,
            job.KeepArtifacts);
    }

    private static async Task<IResult> CreateJobAsync(
        HttpContext http,
        JobStore store,
        JobQueue queue,
        IConfiguration configuration)
    {
        JobRequest request;
        IFormCollection? form;
        try
        {
            (request, form) = await ReadRequestAsync(http);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "Could not parse job request.", detail = ex.Message });
        }

        OverlayCredentialHeaders(http.Request.Headers, request);

        var studioDefaults = configuration.GetSection(StudioOptions.SectionName).Get<StudioOptions>() ?? new StudioOptions();
        var ollamaDefaults = configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>() ?? new OllamaOptions();
        var (studio, model) = JobOptionsMerger.Apply(studioDefaults, ollamaDefaults, request);
        var orchestration = JobOptionsMerger.NormalizeOrchestration(request.Orchestration);

        var outputRoot = JobPaths.ResolveOutputRoot(studioDefaults.OutputDirectory, Directory.GetCurrentDirectory());
        var jobId = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var jobDirectory = JobPaths.JobDirectory(outputRoot, jobId);
        Directory.CreateDirectory(JobPaths.InputsDirectory(jobDirectory));

        var transcriptPath = await SaveUploadOrFallbackAsync(
            form,
            ["transcript", "TranscriptPath", "Transcript_file", "Transcript file"],
            JobPaths.TranscriptFile(jobDirectory),
            FindDefaultTranscript());
        if (string.IsNullOrWhiteSpace(transcriptPath))
        {
            transcriptPath = WriteTextIfPresent(request.TranscriptText, JobPaths.TranscriptFile(jobDirectory));
        }

        var stubPath = await SaveUploadOrFallbackAsync(
            form,
            ["stubNarration", "stub_narration", "StubNarrationPath", "Stub_narration_file", "Dub_narration_file"],
            JobPaths.StubNarrationFile(jobDirectory),
            null);
        if (string.IsNullOrWhiteSpace(stubPath))
        {
            stubPath = WriteTextIfPresent(request.StubNarrationText, JobPaths.StubNarrationFile(jobDirectory));
        }

        if (string.IsNullOrWhiteSpace(transcriptPath))
        {
            return Results.BadRequest(new { error = "Transcript file is required. Upload 'transcript' or place voice_transcript.txt in the Studio working directory." });
        }

        if (studio.UseStubNarration && string.IsNullOrWhiteSpace(stubPath) && !StubExistsOnDisk(studio, transcriptPath))
        {
            return Results.BadRequest(new { error = "Stub narration file is required when UseStubNarration is true. Upload 'stubNarration'." });
        }

        studio.TranscriptPath = transcriptPath;
        studio.OutputDirectory = jobDirectory;
        if (!string.IsNullOrWhiteSpace(stubPath))
        {
            studio.StubNarrationPath = stubPath;
        }

        var job = new ProductionJob
        {
            JobId = jobId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim(),
            Orchestration = orchestration,
            CallbackUrl = string.IsNullOrWhiteSpace(request.CallbackUrl) ? null : request.CallbackUrl.Trim(),
            KeepArtifacts = request.KeepArtifacts ?? true,
            Studio = studio,
            Model = model,
            OutputDirectory = jobDirectory,
            TranscriptPath = transcriptPath
        };

        store.Add(job);
        await queue.EnqueueAsync(job.JobId);
        return Results.Json(ToStatus(job, PublicBaseUrl(http)), Json, statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult GetJob(string jobId, HttpContext http, JobStore store)
    {
        var job = store.Get(jobId);
        return job is null
            ? Results.NotFound(new { error = $"Job '{jobId}' was not found." })
            : Results.Json(ToStatus(job, PublicBaseUrl(http)), Json);
    }

    private static IResult GetEvents(string jobId, JobStore store)
    {
        var job = store.Get(jobId);
        if (job is null)
        {
            return Results.NotFound(new { error = $"Job '{jobId}' was not found." });
        }

        if (string.IsNullOrWhiteSpace(job.EventsPath) || !File.Exists(job.EventsPath))
        {
            return Results.Json(new { jobId, events = Array.Empty<string>() }, Json);
        }

        var lines = File.ReadAllLines(job.EventsPath);
        return Results.Json(new { jobId, events = lines }, Json);
    }

    private static async Task<IResult> DownloadMerged(string jobId, JobStore store)
    {
        var job = store.Get(jobId);
        if (job is null)
        {
            return Results.NotFound(new { error = $"Job '{jobId}' was not found." });
        }

        if (string.IsNullOrWhiteSpace(job.MergedVideoPath) || !File.Exists(job.MergedVideoPath))
        {
            return Results.NotFound(new { error = "Merged video is not available yet." });
        }

        var fileName = Path.GetFileName(job.MergedVideoPath);
        var bytes = await File.ReadAllBytesAsync(job.MergedVideoPath);
        if (!job.KeepArtifacts)
        {
            TryDeleteDirectory(job.OutputDirectory);
        }

        return Results.File(bytes, "video/mp4", fileName);
    }

    private static async Task<(JobRequest Request, IFormCollection? Form)> ReadRequestAsync(HttpContext http)
    {
        if (http.Request.HasJsonContentType())
        {
            var fromJson = await http.Request.ReadFromJsonAsync<JobRequest>(Json);
            return (fromJson ?? new JobRequest(), null);
        }

        if (!http.Request.HasFormContentType)
        {
            return (new JobRequest(), null);
        }

        var form = await http.Request.ReadFormAsync();
        var payload = FirstForm(form, "payload", "request", "json");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            return (JsonSerializer.Deserialize<JobRequest>(payload, Json) ?? new JobRequest(), form);
        }

        return (new JobRequest
        {
            Title = FirstForm(form, "title", "Title"),
            Orchestration = FirstForm(form, "orchestration", "Orchestration") ?? "direct",
            CallbackUrl = FirstForm(form, "callbackUrl", "CallbackUrl"),
            KeepArtifacts = ParseBool(FirstForm(form, "keepArtifacts", "KeepArtifacts"), true),
            Endpoint = FirstForm(form, "endpoint", "Endpoint"),
            Model = FirstForm(form, "model", "Model"),
            TimeoutMinutes = ParseInt(FirstForm(form, "timeoutMinutes", "TimeoutMinutes")),
            SegmentCount = ParseInt(FirstForm(form, "segmentCount", "SegmentCount")),
            MaxSlidesPerSegment = ParseInt(FirstForm(form, "maxSlidesPerSegment", "MaxSlidesPerSegment")),
            TargetTalkMinutes = ParseInt(FirstForm(form, "targetTalkMinutes", "TargetTalkMinutes")),
            UseStubNarration = ParseBoolNullable(FirstForm(form, "useStubNarration", "UseStubNarration")),
            UseStubSlidePhrases = ParseBoolNullable(FirstForm(form, "useStubSlidePhrases", "UseStubSlidePhrases")),
            OpenAIModel = FirstForm(form, "openAIModel", "OpenAIModel"),
            OpenAIApiKey = FirstForm(form, "openAIApiKey", "OpenAIApiKey"),
            AzureOpenAIEndpoint = FirstForm(form, "azureOpenAIEndpoint", "AzureOpenAIEndpoint"),
            AzureOpenAIDeployment = FirstForm(form, "azureOpenAIDeployment", "AzureOpenAIDeployment"),
            AzureOpenAIApiKey = FirstForm(form, "azureOpenAIApiKey", "AzureOpenAIApiKey"),
            ElevenLabsApiKey = FirstForm(form, "elevenLabsApiKey", "ElevenLabsApiKey"),
            ElevenLabsVoiceId = FirstForm(form, "elevenLabsVoiceId", "ElevenLabsVoiceId"),
            ElevenLabsModelId = FirstForm(form, "elevenLabsModelId", "ElevenLabsModelId"),
            VideoWidth = ParseInt(FirstForm(form, "videoWidth", "VideoWidth")),
            VideoHeight = ParseInt(FirstForm(form, "videoHeight", "VideoHeight")),
            VideoFrameRate = ParseInt(FirstForm(form, "videoFrameRate", "VideoFrameRate")),
            RewriteExtraInstructions = FirstForm(form, "rewriteExtraInstructions", "RewriteExtraInstructions")
        }, form);
    }

    private static void OverlayCredentialHeaders(IHeaderDictionary headers, JobRequest request)
    {
        Overlay(headers, "X-ElevenLabs-Api-Key", value => request.ElevenLabsApiKey ??= value);
        Overlay(headers, "X-OpenAI-Api-Key", value => request.OpenAIApiKey ??= value);
        Overlay(headers, "X-Azure-OpenAI-Api-Key", value => request.AzureOpenAIApiKey ??= value);
    }

    private static void Overlay(IHeaderDictionary headers, string name, Action<string> apply)
    {
        if (headers.TryGetValue(name, out var values))
        {
            var value = values.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                apply(value.Trim());
            }
        }
    }

    private static async Task<string?> SaveUploadOrFallbackAsync(
        IFormCollection? form,
        IReadOnlyList<string> names,
        string destination,
        string? fallbackPath)
    {
        if (form is not null)
        {
            foreach (var name in names)
            {
                var file = form.Files.GetFile(name);
                if (file is { Length: > 0 })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var stream = File.Create(destination);
                    await file.CopyToAsync(stream);
                    return destination;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackPath) && File.Exists(fallbackPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(fallbackPath, destination, overwrite: true);
            return destination;
        }

        return null;
    }

    private static string? FindDefaultTranscript()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "voice_transcript.txt"),
            Path.Combine(AppContext.BaseDirectory, "voice_transcript.txt")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? WriteTextIfPresent(string? text, string destination)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, text);
        return destination;
    }

    private static bool StubExistsOnDisk(StudioOptions studio, string transcriptPath)
    {
        var configured = string.IsNullOrWhiteSpace(studio.StubNarrationPath)
            ? "voice_transcript_ollama.txt"
            : studio.StubNarrationPath;
        if (Path.IsPathRooted(configured) && File.Exists(configured))
        {
            return true;
        }

        var transcriptDir = Path.GetDirectoryName(transcriptPath);
        var candidates = new List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), configured),
            Path.Combine(AppContext.BaseDirectory, configured)
        };
        if (!string.IsNullOrWhiteSpace(transcriptDir))
        {
            candidates.Insert(0, Path.Combine(transcriptDir, configured));
        }

        return candidates.Any(File.Exists);
    }

    private static string? FirstForm(IFormCollection form, params string[] names)
    {
        foreach (var name in names)
        {
            if (form.TryGetValue(name, out var values))
            {
                var text = values.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private static bool ParseBool(string? value, bool defaultValue) =>
        ParseBoolNullable(value) ?? defaultValue;

    private static bool? ParseBoolNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value is "1" or "yes" or "on" ? true : value is "0" or "no" or "off" ? false : null;
    }

    private static string PublicBaseUrl(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-Prefix"].ToString();
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        return string.IsNullOrWhiteSpace(forwarded) ? baseUrl : baseUrl + forwarded.TrimEnd('/');
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // leftover artifacts are acceptable; operator can delete later
        }
    }
}

public sealed record JobStatusResponse(
    string JobId,
    string Status,
    string? Title,
    string Orchestration,
    string? Error,
    string? MergedVideoPath,
    string? MergedVideoUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    bool KeepArtifacts);
