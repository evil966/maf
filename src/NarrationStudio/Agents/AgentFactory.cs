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

        var memoryRoot = Path.Combine(_store.OutputDirectory, "agent-file-memory");
        var fileMemory = new FileMemoryProvider(
            new FileSystemAgentFileStore(memoryRoot),
            _ => new FileMemoryState { WorkingFolder = "production-notes" });

        var guardrails = new ProductionGuardrails(_options, _loggerFactory.CreateLogger<ProductionGuardrails>());
        IList<AIContextProvider> providers = [_context, fileMemory];

        var coordinator = Create(
            chatClient,
            guardrails,
            providers,
            id: "coordinator",
            name: "coordinator",
            description: "Routes the narration-to-video pipeline to the right specialist.",
            instructions:
            """
            You coordinate a narration-to-video factory.
            Always call GetPipelineStatus first.
            Then hand off:
            - narration_rewriter if the 7-minute Ollama talk does not exist
            - script_segmenter if eight narration scripts do not exist
            - slide_planner if slides are not planned
            - image_renderer if PNG slides are missing
            - tts_producer if audio-01.mp3 through audio-08.mp3 are missing
            - video_composer if segment videos or the merged video are missing
            When the merged video exists, tell the user the full path and stop.
            Never skip a specialist. Never invent file names.
            Later steps must use the rewritten narration, never the raw transcript.
            """,
            tools: [AIFunctionFactory.Create(_tools.GetPipelineStatus)]);

        var narrationRewriter = Create(
            chatClient,
            guardrails,
            providers,
            id: "narration-rewriter",
            name: "narration_rewriter",
            description: "Rephrases voice_transcript.txt through local Ollama into a 7-minute executive talk.",
            instructions:
            """
            You prepare the spoken script for the rest of the pipeline.
            Call LoadTranscript if you need to inspect the source, then always call RephraseTranscriptIntoNarration.
            That tool submits the transcript to Ollama and writes the 7-minute talk.
            Do not segment, design slides, or create audio yourself.
            After the rewritten narration exists, hand off to script_segmenter or back to coordinator.
            """,
            tools:
            [
                AIFunctionFactory.Create(_tools.GetPipelineStatus),
                AIFunctionFactory.Create(_tools.LoadTranscript),
                AIFunctionFactory.Create(_tools.RephraseTranscriptIntoNarration)
            ]);

        var scriptSegmenter = Create(
            chatClient,
            guardrails,
            providers,
            id: "script-segmenter",
            name: "script_segmenter",
            description: "Splits the rewritten 7-minute narration into eight scripts for TTS.",
            instructions:
            """
            You turn the rewritten 7-minute talk into exactly 8 narration scripts.
            Call LoadNarrationSource (not the raw transcript), then SegmentTranscriptIntoEightScripts.
            You may refine scripts with SaveNarrationScripts only if you still produce exactly 8 items.
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
            description: "Plans at most five on-screen phrases per narration segment.",
            instructions:
            """
            Create slide phrases for each of the 8 narration scripts.
            Call PlanSlidesForAllSegments. Each segment may have at most 5 slides.
            Each slide phrase must describe that part of the narration.
            Then hand off to image_renderer, or back to coordinator if images already exist.
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
            Render one PNG slide for every planned phrase.
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
            description: "Creates audio-01.mp3 through audio-08.mp3 from the narration scripts.",
            instructions:
            """
            After the 8 narration scripts exist, synthesize one MP3 per script.
            Call SynthesizeAllAudioAsync. File names must be audio-01.mp3 ... audio-08.mp3.
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
            description: "Syncs slides to each MP3, then merges the eight videos.",
            instructions:
            """
            Create video-01.mp4 ... video-08.mp4 by syncing each segment's slides to that segment's MP3.
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
        var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
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

        _logger.LogInformation("Using local Ollama for agent orchestration.");
        return _ollamaFactory.Create();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
