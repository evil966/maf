using System.Threading.Channels;

namespace NarrationStudio.Service;

public sealed record SessionStepWork(string JobId, string Step);

public sealed class StepQueue
{
    private readonly Channel<SessionStepWork> _channel = Channel.CreateUnbounded<SessionStepWork>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(SessionStepWork work, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(work, cancellationToken);

    public IAsyncEnumerable<SessionStepWork> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
