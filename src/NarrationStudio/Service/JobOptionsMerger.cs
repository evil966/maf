using NarrationStudio.Configuration;

namespace NarrationStudio.Service;

public static class JobOptionsMerger
{
    public static StudioOptions CloneStudio(StudioOptions source) => new()
    {
        SegmentCount = source.SegmentCount,
        MaxSlidesPerSegment = source.MaxSlidesPerSegment,
        TranscriptPath = source.TranscriptPath,
        OutputDirectory = source.OutputDirectory,
        OpenAIModel = source.OpenAIModel,
        OpenAIApiKey = source.OpenAIApiKey,
        AzureOpenAIEndpoint = source.AzureOpenAIEndpoint,
        AzureOpenAIDeployment = source.AzureOpenAIDeployment,
        AzureOpenAIApiKey = source.AzureOpenAIApiKey,
        ElevenLabsApiKey = source.ElevenLabsApiKey,
        ElevenLabsVoiceId = source.ElevenLabsVoiceId,
        ElevenLabsModelId = source.ElevenLabsModelId,
        VideoWidth = source.VideoWidth,
        VideoHeight = source.VideoHeight,
        VideoFrameRate = source.VideoFrameRate,
        TargetTalkMinutes = source.TargetTalkMinutes,
        UseStubNarration = source.UseStubNarration,
        UseStubSlidePhrases = source.UseStubSlidePhrases,
        StubNarrationPath = source.StubNarrationPath,
        RewriteExtraInstructions = source.RewriteExtraInstructions
    };

    public static OllamaOptions CloneOllama(OllamaOptions source) => new()
    {
        Endpoint = source.Endpoint,
        Model = source.Model,
        TimeoutMinutes = source.TimeoutMinutes
    };

    public static (StudioOptions Studio, OllamaOptions Model) Apply(
        StudioOptions studioDefaults,
        OllamaOptions ollamaDefaults,
        JobRequest request)
    {
        var studio = CloneStudio(studioDefaults);
        var model = CloneOllama(ollamaDefaults);

        if (request.SegmentCount is int segments)
        {
            studio.SegmentCount = Math.Max(1, segments);
        }

        if (request.MaxSlidesPerSegment is int maxSlides)
        {
            studio.MaxSlidesPerSegment = Math.Clamp(maxSlides, 1, 5);
        }

        if (request.TargetTalkMinutes is int minutes)
        {
            studio.TargetTalkMinutes = Math.Max(1, minutes);
        }

        if (request.UseStubNarration is bool stubNarration)
        {
            studio.UseStubNarration = stubNarration;
        }

        if (request.UseStubSlidePhrases is bool stubSlides)
        {
            studio.UseStubSlidePhrases = stubSlides;
        }

        Overlay(request.OpenAIModel, value => studio.OpenAIModel = value);
        Overlay(request.OpenAIApiKey, value => studio.OpenAIApiKey = value);
        Overlay(request.AzureOpenAIEndpoint, value => studio.AzureOpenAIEndpoint = value);
        Overlay(request.AzureOpenAIDeployment, value => studio.AzureOpenAIDeployment = value);
        Overlay(request.AzureOpenAIApiKey, value => studio.AzureOpenAIApiKey = value);
        Overlay(request.ElevenLabsApiKey, value => studio.ElevenLabsApiKey = value);
        Overlay(request.ElevenLabsVoiceId, value => studio.ElevenLabsVoiceId = value);
        Overlay(request.ElevenLabsModelId, value => studio.ElevenLabsModelId = value);
        Overlay(request.RewriteExtraInstructions, value => studio.RewriteExtraInstructions = value);

        if (request.VideoWidth is int width)
        {
            studio.VideoWidth = Math.Max(1, width);
        }

        if (request.VideoHeight is int height)
        {
            studio.VideoHeight = Math.Max(1, height);
        }

        if (request.VideoFrameRate is int fps)
        {
            studio.VideoFrameRate = Math.Max(1, fps);
        }

        Overlay(request.Endpoint, value => model.Endpoint = value);
        Overlay(request.Model, value => model.Model = value);
        if (request.TimeoutMinutes is int timeout)
        {
            model.TimeoutMinutes = Math.Max(1, timeout);
        }

        return (studio, model);
    }

    public static string NormalizeOrchestration(string? orchestration)
    {
        var value = (orchestration ?? "direct").Trim().ToLowerInvariant();
        return value is "handoff" or "sequential" or "direct" ? value : "direct";
    }

    private static void Overlay(string? incoming, Action<string> apply)
    {
        if (string.IsNullOrWhiteSpace(incoming) || incoming is "undefined" or "null")
        {
            return;
        }

        apply(incoming.Trim());
    }
}
