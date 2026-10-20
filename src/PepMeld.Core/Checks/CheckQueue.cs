using System.Threading.Channels;

namespace PepMeld.Core.Checks;

/// <summary>
/// Hands a new check's id to the worker. Not a queue between users: each user has at most
/// one check running, enforced by the database, so this only decouples the HTTP request
/// from the work. What is in the channel is lost on restart, and that is fine: the worker
/// marks every check still "running" at startup as interrupted, and the user starts again.
/// </summary>
public sealed class CheckQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public void Enqueue(Guid checkId) => channel.Writer.TryWrite(checkId);

    public ChannelReader<Guid> Reader => channel.Reader;
}
