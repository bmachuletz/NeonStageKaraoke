using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Karaoke.Server;

public sealed class ChangeFeedService
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id) => _subscribers.TryRemove(id, out _);
    public void Publish(string change)
    {
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(change);
    }
}
