using System.Threading.Channels;
using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Push;

/// <summary>
/// Carries newly-recorded feed notifications from <c>BridgeStateStore</c> to the push dispatcher.
///
/// A channel rather than a direct call so the store never waits on a push service: writing a
/// notification to the feed must not slow down (or fail with) whatever the network is doing. The
/// channel is bounded and drops the oldest entry when full, so a wedged dispatcher costs stale
/// pushes rather than unbounded memory.
/// </summary>
public sealed class NotificationBroadcaster
{
    private readonly Channel<BridgeNotificationDto> _channel = Channel.CreateBounded<BridgeNotificationDto>(
        new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public void Publish(BridgeNotificationDto notification) => _channel.Writer.TryWrite(notification);

    public IAsyncEnumerable<BridgeNotificationDto> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
