using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NarrationStudio.Configuration;
using NarrationStudio.Guardrails;
using NarrationStudio.Memory;
using NarrationStudio.Services;
using NarrationStudio.Tools;
using OpenAI;
using OpenAI.Chat;

namespace NarrationStudio.Agents;

public sealed record ProductionCrew(
    AIAgent Coordinator,
    AIAgent NarrationRewriter,
    AIAgent ScriptSegmenter,
    AIAgent SlidePlanner,
    AIAgent ImageRenderer,
    AIAgent TtsProducer,
    AIAgent VideoComposer);

public sealed class AgentFactory
{
    private readonly StudioOptions _options;
    private readonly PipelineStore _store;
    private readonly ProductionTools _tools;
    private readonly ProductionContextProvider _context;
    private readonly OllamaChatClientFactory _ollamaFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentFactory> _logger;

    public AgentFactory(
        IOptions<StudioOptions> options,
        PipelineStore store,
        ProductionTools tools,
        ProductionContextProvider context,
        OllamaChatClientFactory ollamaFactory,
        ILoggerFactory loggerFactory,
        ILogger<AgentFactory> logger)
    {
        _options = options.Value;
        _store = store;
        _tools = tools;
        _context = context;
        _ollamaFactory = ollamaFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public bool TryCreate(out ProductionCrew? crew)
    {
        crew = null;
        IChatClient? chatClient;
        try
        {
            chatClient = CreateChatClient();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create a chat client for agent orchestration.");
            return false;
        }

        if (chatClient is null)
        {
            _logger.LogWarning("No Ollama, OpenAI, or Azure OpenAI client is available. Agent orchestration is unavailable.");
            return false;
        }

        Directory.CreateDirectory(_store.AgentMemoryDirectory);
        var fileMemory = new FileMemoryProvider(
            new FileSystemAgentFileStore(_store.AgentMemoryRoot),
            _ => new FileMemoryState { WorkingFolder = _store.AgentMemoryWorkingFolder });
        _logger.LogInformation("Agent file memory for this run: {Path}", _store.AgentMemoryDirectory);

        var guardrails = new ProductionGuardrails(_loggerFactory.CreateLogger<ProductionGuardrails>());
        IList<AIContextProvider> providers = [_context, fileMemory];

        var segments = _options.SegmentCount;
        var maxSlides = _options.MaxSlidesPerSegment;
        var minutes = _options.TargetTalkMinutes;
        var audioRange = StudioNaming.AudioRange(segments);
        var videoRange = StudioNaming.VideoRange(segments);

        var coordinator = Create(
            chatClient,
            guardrails,
            providers,
            id: "coordinator",
            name: "coordinator",
            description: "Routes the narration-to-video pipeline to the right specialist.",
            instructions:
            $"""
            You coordinate a narration-to-video factory.
            Previous generated files were deleted at the start of this run, so nothing is already finished.
            Always call GetPipelineStatus first.
            Then hand off every specialist in order until the merged video for THIS run exists:
            - narration_rewriter only if the {minutes}-minute talk does not exist and stubbing is off
            - script_segmenter if {segments} narration scripts do not exist (always from the rewritten narration, never the raw transcript)
            - slide_planner to plan slides for the current scripts
            - image_renderer to render PNG slides
            - tts_producer to synthesize {audioRange}
            - video_composer to compose segment videos and merge them
            When the merged video exists, tell the user the full path and stop.
            Never skip a specialist. Never invent file names. Never reuse leftover files from an earlier run.
            Later steps must use the rewritten narration, never the raw transcript.
            """,
            tools: [AIFunctionFactory.Create(_tools.GetPipelineStatus)]);

        var narrationRewriter = Create(
            chatClient,
            guardrails,
            providers,
            id: "narration-rewriter",
            name: "narration_rewriter",
            description: $"Rephrases voice_transcript.txt through local Ollama into a {minutes}-minute executive talk.",
            instructions:
            $"""
            You prepare the spoken script for the rest of the pipeline.
            Always call RephraseTranscriptIntoNarration. Do not rewrite the transcript yourself.
            Do not call LoadTranscript. The tool loads the source and, when stubbing is on, uses voice_transcript_ollama.txt.
            Do not segment, design slides, or create audio yourself.
            After the rewritten narration exists, hand off to script_segmenter or back to coordinator.
            """,
            tools:
            [
                AIFunctionFactory.Create(_tools.GetPipelineStatus),
                AIFunctionFactory.Create(_tools.RephraseTranscriptIntoNarration)
            ]);

        var scriptSegmenter = Create(
            chatClient,
            guardrails,
            providers,
            id: "script-segmenter",
            name: "script_segmenter",
            description: $"Splits the rewritten {minutes}-minute narration into {segments} scripts for TTS.",
            instructions:
            $"""
            You turn the rewritten {minutes}-minute talk into exactly {segments} narration scripts.
            Call LoadNarrationSource (not the raw transcript), then SegmentTranscriptIntoEightScripts.
            You may refine scripts with SaveNarrationScripts only if you still produce exactly {segments} items.
            After the scripts exist, hand control back to coordinator.
            """,
            tools:
            [
                AIFunctionFactory.Create(_tools.GetPipelineStatus),
                AIFunctionFactory.Create(_tools.LoadNarrationSource),
                AIFunctionFactory.Create(_tools.SegmentTranscriptIntoEightScripts),
                AIFunctionFactory.Create(_tools.SaveNarrationScripts)
            ]);

        var slidePlanner = Create(
            chatClient,
            guardrails,
            providers,
            id: "slide-planner",
            name: "slide_planner",
            description: $"Plans at most {maxSlides} on-screen phrases per narration segment.",
            instructions:
            $"""
            Create slide phrases for each of the {segments} narration scripts.
            Call PlanSlidesForAllSegments. Each segment may have at most {maxSlides} slides.
            That tool splits the script, then {(_options.UseStubSlidePhrases ? "uses the excerpt as the on-screen sentence" : "Ollama rewrites each excerpt as one sentence for the slide image")}.
            Do not invent slide text yourself. Always re-plan for this run, then hand off to image_renderer.
            """,
            tools:
            [
                AIFunctionFactory.Create(_tools.GetPipelineStatus),
                AIFunctionFactory.Create(_tools.PlanSlidesForAllSegments)
            ]);

        var imageRenderer = Create(
            chatClient,
            guardrails,
            providers,
            id: "image-renderer",
            name: "image_renderer",
            description: "Renders PNG slides containing the planned phrase text.",
            instructions:
            """
            Render one PNG slide for every planned phrase. Always overwrite previous PNGs.
            Each PNG must show the on-screen sentence stored on the slide, not the raw excerpt.
            Call RenderAllSlideImages, then hand control back to coordinator.
            """,
            tools:
            [
                AIFunctionFactory.Create(_tools.GetPipelineStatus),
                AIFunctionFactory.Create(_tools.RenderAllSlideImages)
            ]);

        var ttsProducer = Create(
            chatClient,
            guardrails,
            providers,
            id: "tts-producer",
            name: "tts_producer",
            description: $"Creates {audioRange} from the narration scripts.",
            instructions:
            $"""
            After the {segments} narration scripts exist, synthesize one MP3 per script. Always overwrite previous MP3s.
            Call SynthesizeAllAudioAsync. File names must be {audioRange}.
            Then hand control back to coordinator.
            """,
            tools:
            [
                AIFunctionFactory.Create(_tools.GetPipelineStatus),
                AIFunctionFactory.Create(_tools.SynthesizeAllAudioAsync)
            ]);

        var videoComposer = Create(
            chatClient,
            guardrails,
            providers,
            id: "video-composer",
            name: "video_composer",
            description: $"Syncs slides to each MP3, then merges the {segments} videos.",
            instructions:
            $"""
            Create {videoRange} by syncing each segment's slides to that segment's MP3.
            Always overwrite previous segment videos, then merge.
            Call ComposeAllSegmentVideos, then MergeAllVideos.
            The merged file must use the timestamped name from the pipeline.
            Then hand control back to coordinator.
            """,
            tools:
            [
                AIFunctionFactory.Create(_tools.GetPipelineStatus),
                AIFunctionFactory.Create(_tools.ComposeAllSegmentVideos),
                AIFunctionFactory.Create(_tools.MergeAllVideos)
            ]);

        crew = new ProductionCrew(coordinator, narrationRewriter, scriptSegmenter, slidePlanner, imageRenderer, ttsProducer, videoComposer);
        return true;
    }

    private static AIAgent Create(
        IChatClient chatClient,
        ProductionGuardrails guardrails,
        IList<AIContextProvider> providers,
        string id,
        string name,
        string description,
        string instructions,
        IList<AITool> tools)
    {
        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = id,
            Name = name,
            Description = description,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = tools
            },
            AIContextProviders = providers
        });

        return agent
            .AsBuilder()
            .Use(guardrails.RunAsync, null)
            .Use(guardrails.FunctionAsync)
            .Build();
    }

    private IChatClient? CreateChatClient()
    {
        var azureEndpoint = FirstNonEmpty(
            _options.AzureOpenAIEndpoint,
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"));
        var azureDeployment = FirstNonEmpty(
            _options.AzureOpenAIDeployment,
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")) ?? "gpt-4o-mini";
        var azureKey = FirstNonEmpty(
            _options.AzureOpenAIApiKey,
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"));
        var openAiKey = FirstNonEmpty(
            _options.OpenAIApiKey,
            Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var model = FirstNonEmpty(_options.OpenAIModel, Environment.GetEnvironmentVariable("OPENAI_MODEL")) ?? "gpt-4o-mini";

        if (!string.IsNullOrWhiteSpace(azureEndpoint))
        {
            AzureOpenAIClient azureClient = string.IsNullOrWhiteSpace(azureKey)
                ? new AzureOpenAIClient(new Uri(azureEndpoint), new DefaultAzureCredential())
                : new AzureOpenAIClient(new Uri(azureEndpoint), new ApiKeyCredential(azureKey));
            _logger.LogInformation("Using Azure OpenAI deployment {Deployment} for agent orchestration.", azureDeployment);
            return azureClient.GetChatClient(azureDeployment).AsIChatClient();
        }

        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            _logger.LogInformation("Using OpenAI model {Model} for agent orchestration.", model);
            return new OpenAIClient(openAiKey).GetChatClient(model).AsIChatClient();
        }

        _logger.LogInformation(
            _options.UseStubNarration
                ? "Using local Ollama for specialist agents only. Narration rewrite is stubbed from voice_transcript_ollama.txt."
                : "Using local Ollama for agent orchestration.");
        return _ollamaFactory.Create();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
