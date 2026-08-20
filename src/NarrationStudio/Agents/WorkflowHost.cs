using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
        return AgentWorkflowBuilder.CreateHandoffBuilderWith(crew.Coordinator)
            .WithHandoff(crew.Coordinator, crew.NarrationRewriter, "The raw transcript has not been rephrased into a 7-minute executive talk.")
            .WithHandoff(crew.NarrationRewriter, crew.ScriptSegmenter, "The 7-minute narration is ready to split into 8 scripts.")
            .WithHandoff(crew.Coordinator, crew.ScriptSegmenter, "The rewritten narration exists but has not been split into 8 scripts.")
            .WithHandoff(crew.Coordinator, crew.SlidePlanner, "Narration scripts exist but slide phrases are missing.")
            .WithHandoff(crew.Coordinator, crew.ImageRenderer, "Slide phrases exist but PNG slides are missing.")
            .WithHandoff(crew.Coordinator, crew.TtsProducer, "Narration scripts exist but audio-01.mp3 through audio-08.mp3 are missing.")
            .WithHandoff(crew.Coordinator, crew.VideoComposer, "Audio and slides exist, so segment videos or the merged video can be created.")
            .WithHandoff(crew.SlidePlanner, crew.ImageRenderer, "Slide phrases are ready and should be rendered to PNG files.")
            .WithHandoffs(
                [crew.NarrationRewriter, crew.ScriptSegmenter, crew.SlidePlanner, crew.ImageRenderer, crew.TtsProducer, crew.VideoComposer],
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
        return AgentWorkflowBuilder.BuildSequential(
            "narration-studio-sequential",
            [
                crew.NarrationRewriter,
                crew.ScriptSegmenter,
                crew.SlidePlanner,
                crew.ImageRenderer,
                crew.TtsProducer,
                crew.VideoComposer
            ]);
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

    public string BuildUserPrompt(string transcriptPath) =>
        $"""
        Produce a complete narration video from this transcript file:
        {transcriptPath}

        Required workflow:
        1. Submit voice_transcript.txt to Ollama and rewrite it as a 7-minute talk for a mixed audience leaning toward executives.
        2. Segment THAT rewritten narration into 8 narration scripts. Do not segment the raw transcript.
        3. Create at most 5 image slides per script. Each slide contains a phrase covering part of that script.
        4. When the 8 scripts exist, create audio-01.mp3 ... audio-08.mp3.
        5. Sync each MP3 with that segment's slides into video-01.mp4 ... video-08.mp4.
        6. Merge the eight videos into {_store.MergedVideoFileName()}.

        Use your tools. Handoff to the specialist who owns the next unfinished step.
        Current status:
        {_store.SummarizeForAgent()}
        """;
}
