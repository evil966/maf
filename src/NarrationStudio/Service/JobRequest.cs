using System.Text.Json.Serialization;

namespace NarrationStudio.Service;

public sealed class JobRequest
{
    public string? Title { get; set; }
    public string Orchestration { get; set; } = "direct";
    public string? CallbackUrl { get; set; }

    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? KeepArtifacts { get; set; } = true;

    public string? Endpoint { get; set; }
    public string? Model { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? TimeoutMinutes { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? SegmentCount { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? MaxSlidesPerSegment { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? TargetTalkMinutes { get; set; }

    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? UseStubNarration { get; set; }

    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? UseStubSlidePhrases { get; set; }

    public string? TranscriptText { get; set; }
    public string? StubNarrationText { get; set; }
    public string? OpenAIModel { get; set; }
    public string? OpenAIApiKey { get; set; }
    public string? AzureOpenAIEndpoint { get; set; }
    public string? AzureOpenAIDeployment { get; set; }
    public string? AzureOpenAIApiKey { get; set; }
    public string? ElevenLabsApiKey { get; set; }
    public string? ElevenLabsVoiceId { get; set; }
    public string? ElevenLabsModelId { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? VideoWidth { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? VideoHeight { get; set; }

    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? VideoFrameRate { get; set; }
    public string? RewriteExtraInstructions { get; set; }
}
