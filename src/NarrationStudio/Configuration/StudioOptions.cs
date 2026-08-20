namespace NarrationStudio.Configuration;

public sealed class StudioOptions
{
    public const string SectionName = "Studio";

    public int SegmentCount { get; set; } = 8;
    public int MaxSlidesPerSegment { get; set; } = 5;
    public string TranscriptPath { get; set; } = "";
    public string OutputDirectory { get; set; } = "output";
    public string OpenAIModel { get; set; } = "gpt-4o-mini";
    public string AzureOpenAIEndpoint { get; set; } = "";
    public string AzureOpenAIDeployment { get; set; } = "gpt-4o-mini";
    public string ElevenLabsVoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM";
    public string ElevenLabsModelId { get; set; } = "eleven_multilingual_v2";
    public int VideoWidth { get; set; } = 1920;
    public int VideoHeight { get; set; } = 1080;
    public int VideoFrameRate { get; set; } = 30;
    public int TargetTalkMinutes { get; set; } = 7;
    public bool UseStubNarration { get; set; } = true;
    public string StubNarrationPath { get; set; } = "voice_transcript_ollama.txt";
}
