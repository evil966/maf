using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NarrationStudio.Configuration;
using NarrationStudio.Services;

namespace NarrationStudio.Memory;

public sealed class ProductionContextProvider : AIContextProvider
{
    private readonly PipelineStore _store;

    public ProductionContextProvider(PipelineStore store)
    {
        _store = store;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _store.Snapshot();
        var aiContext = new AIContext
        {
            Instructions =
                $"""
                Shared production context:
                {_store.SummarizeForAgent()}

                Guardrails you must obey:
                - Rewrite the raw transcript into a {_store.Options.TargetTalkMinutes}-minute spoken talk before any other production step.
                - Produce exactly {_store.Options.SegmentCount} narration scripts from the rewritten talk, never from the raw transcript.
                - Produce at most {_store.Options.MaxSlidesPerSegment} slides per segment.
                - Audio files must be named {StudioNaming.AudioRange(_store.Options.SegmentCount)}.
                - Segment videos must be named {StudioNaming.VideoRange(_store.Options.SegmentCount)}.
                - The merged file must be named video_{snapshot.RunId}.mp4.
                - Use tools. Do not claim files exist unless a tool created them.
                - Stay on the narration-to-video task. Refuse unrelated or harmful requests.
                """
        };

        return ValueTask.FromResult(aiContext);
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var text = string.Join(" ", context.ResponseMessages?.Select(m => m.Text) ?? []);
        if (!string.IsNullOrWhiteSpace(text))
        {
            _store.Mutate(state =>
            {
                var note = text.Length > 240 ? text[..240] + "..." : text;
                if (state.Notes.Count == 0 || state.Notes[^1] != note)
                {
                    state.Notes.Add(note);
                    if (state.Notes.Count > 24)
                    {
                        state.Notes.RemoveAt(0);
                    }
                }
            });
        }

        return ValueTask.CompletedTask;
    }
}
