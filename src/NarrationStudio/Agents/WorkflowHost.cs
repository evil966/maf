using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NarrationStudio.Configuration;
using NarrationStudio.Services;

namespace NarrationStudio.Agents;

public sealed class WorkflowHost
{
    private readonly PipelineStore _store;
    private readonly ILogger<WorkflowHost> _logger;

    public WorkflowHost(PipelineStore store, ILogger<WorkflowHost> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Workflow CreateHandoffWorkflow(ProductionCrew crew)
    {
        var segments = _store.Options.SegmentCount;
        var minutes = _store.Options.TargetTalkMinutes;
        var audioRange = StudioNaming.AudioRange(segments);
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(crew.Coordinator);

        if (!_store.Options.UseStubNarration)
        {
            builder = builder
                .WithHandoff(crew.Coordinator, crew.NarrationRewriter, $"The raw transcript has not been rephrased into a {minutes}-minute executive talk.")
                .WithHandoff(crew.NarrationRewriter, crew.ScriptSegmenter, $"The {minutes}-minute narration is ready to split into {segments} scripts.");
        }

        return builder
            .WithHandoff(crew.Coordinator, crew.ScriptSegmenter, $"The rewritten narration exists but has not been split into {segments} scripts.")
            .WithHandoff(crew.Coordinator, crew.SlidePlanner, "Narration scripts exist but slide phrases are missing.")
            .WithHandoff(crew.Coordinator, crew.ImageRenderer, "Slide phrases exist but PNG slides are missing.")
            .WithHandoff(crew.Coordinator, crew.TtsProducer, $"Narration scripts exist but {audioRange} are missing.")
            .WithHandoff(crew.Coordinator, crew.VideoComposer, "Audio and slides exist, so segment videos or the merged video can be created.")
            .WithHandoff(crew.SlidePlanner, crew.ImageRenderer, "Slide phrases are ready and should be rendered to PNG files.")
            .WithHandoffs(
                _store.Options.UseStubNarration
                    ? [crew.ScriptSegmenter, crew.SlidePlanner, crew.ImageRenderer, crew.TtsProducer, crew.VideoComposer]
                    : [crew.NarrationRewriter, crew.ScriptSegmenter, crew.SlidePlanner, crew.ImageRenderer, crew.TtsProducer, crew.VideoComposer],
                crew.Coordinator,
                "Return to the coordinator after the specialist finishes its tools.")
            .WithAutonomousMode(
                turnLimit: 8,
                continuationPrompt: "Continue from GetPipelineStatus. Handoff to the next specialist, or stop if the merged video exists.",
                agents: [crew.Coordinator])
            .Build();
    }

    public Workflow CreateSequentialWorkflow(ProductionCrew crew)
    {
        AIAgent[] agents = _store.Options.UseStubNarration
            ?
            [
                crew.ScriptSegmenter,
                crew.SlidePlanner,
                crew.ImageRenderer,
                crew.TtsProducer,
                crew.VideoComposer
            ]
            :
            [
                crew.NarrationRewriter,
                crew.ScriptSegmenter,
                crew.SlidePlanner,
                crew.ImageRenderer,
                crew.TtsProducer,
                crew.VideoComposer
            ];

        return AgentWorkflowBuilder.BuildSequential("narration-studio-sequential", agents);
    }

    public async Task RunAsync(Workflow workflow, string userPrompt, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, userPrompt) };
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages, cancellationToken: cancellationToken);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        string? lastExecutor = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cancellationToken))
        {
            switch (evt)
            {
                case AgentResponseUpdateEvent update:
                    if (update.ExecutorId != lastExecutor)
                    {
                        lastExecutor = update.ExecutorId;
                        Console.WriteLine();
                        Console.WriteLine($"[{update.ExecutorId}]");
                    }

                    if (!string.IsNullOrEmpty(update.Update.Text))
                    {
                        Console.Write(update.Update.Text);
                    }

                    break;
                case WorkflowOutputEvent output:
                    Console.WriteLine();
                    Console.WriteLine("Workflow output received.");
                    _logger.LogInformation("Workflow produced output from {Executor}", output.ExecutorId);
                    return;
            }
        }
    }

    public string BuildUserPrompt(string transcriptPath)
    {
        var options = _store.Options;
        var segments = options.SegmentCount;
        var minutes = options.TargetTalkMinutes;
        var maxSlides = options.MaxSlidesPerSegment;
        var rewriteStep = options.UseStubNarration
            ? $"1. The {minutes}-minute talk is already saved as output/scripts/narration-rewritten.txt from {options.StubNarrationPath}. Do not call Ollama for the talk. Do not use the raw transcript."
            : $"1. Submit voice_transcript.txt to Ollama and rewrite it as a {minutes}-minute talk for a mixed audience leaning toward executives.";

        return
            $"""
            Produce a complete narration video from this transcript file:
            {transcriptPath}

            Required workflow:
            {rewriteStep}
            2. Segment THAT rewritten narration into {segments} narration scripts. Do not segment the raw transcript.
            3. Create at most {maxSlides} image slides per script. Each slide contains a phrase covering part of that script.
            4. When the {segments} scripts exist, create {StudioNaming.AudioRange(segments)}.
            5. Sync each MP3 with that segment's slides into {StudioNaming.VideoRange(segments)}.
            6. Merge the {segments} videos into {_store.MergedVideoFileName()}.

            Use your tools. Handoff to the specialist who owns the next unfinished step.
            Current status:
            {_store.SummarizeForAgent()}
            """;
    }
}
