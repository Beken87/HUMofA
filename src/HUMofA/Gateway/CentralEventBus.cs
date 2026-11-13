using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace HUMofA.Gateway;

/// <summary>
/// Interface for thread-safe message publishing and subscribing across the platform.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Current count of active subscribers.
    /// </summary>
    int SubscriberCount { get; }

    /// <summary>
    /// Total count of messages published through the bus.
    /// </summary>
    long TotalPublishedCount { get; }

    /// <summary>
    /// Publishes a message to all active subscribers.
    /// </summary>
    ValueTask PublishAsync(AgentMessage message, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to all incoming messages.
    /// </summary>
    IAsyncEnumerable<AgentMessage> SubscribeAsync(CancellationToken ct = default);

    /// <summary>
    /// Subscribes to messages filtered by channel name (case-insensitive).
    /// </summary>
    IAsyncEnumerable<AgentMessage> SubscribeAsync(string channel, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to messages with a custom filter predicate.
    /// </summary>
    IAsyncEnumerable<AgentMessage> SubscribeAsync(Func<AgentMessage, bool>? filter, CancellationToken ct = default);

    /// <summary>
    /// Retrieves recent messages from in-memory ring buffer.
    /// </summary>
    IReadOnlyList<AgentMessage> GetRecentHistory(int maxCount = 50);
}

/// <summary>
/// High-throughput, thread-safe central event bus built on System.Threading.Channels.
/// Supports multi-subscriber fan-out and channel-based message filtering.
/// </summary>
public class CentralEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly ConcurrentQueue<AgentMessage> _recentHistory = new();
    private readonly int _historyCapacity;
    private readonly int _subscriberChannelCapacity;
    private long _totalPublishedCount;

    private sealed record Subscription(
        Guid Id,
        Channel<AgentMessage> Channel,
        Func<AgentMessage, bool>? Filter
    );

    public CentralEventBus(int subscriberChannelCapacity = 1000, int historyCapacity = 200)
    {
        _subscriberChannelCapacity = Math.Max(10, subscriberChannelCapacity);
        _historyCapacity = Math.Max(10, historyCapacity);
    }

    public int SubscriberCount => _subscriptions.Count;
    public long TotalPublishedCount => Interlocked.Read(ref _totalPublishedCount);

    public async ValueTask PublishAsync(AgentMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Interlocked.Increment(ref _totalPublishedCount);

        // Record to ring buffer
        _recentHistory.Enqueue(message);
        while (_recentHistory.Count > _historyCapacity && _recentHistory.TryDequeue(out _))
        {
        }

        // Fan out to matching subscribers
        List<Task>? slowTasks = null;

        foreach (var (_, sub) in _subscriptions)
        {
            if (sub.Filter != null && !sub.Filter(message))
            {
                continue;
            }

            // Fast path: attempt immediate non-blocking write
            if (sub.Channel.Writer.TryWrite(message))
            {
                continue;
            }

            // Slow path: subscriber channel buffer is full; fan out write concurrently
            slowTasks ??= new List<Task>();
            slowTasks.Add(WriteToSubscriberAsync(sub.Channel.Writer, message, ct));
        }

        if (slowTasks is { Count: > 0 })
        {
            await Task.WhenAll(slowTasks).ConfigureAwait(false);
        }
    }

    private static async Task WriteToSubscriberAsync(ChannelWriter<AgentMessage> writer, AgentMessage message, CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(message, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Subscriber closed channel
        }
    }

    public IAsyncEnumerable<AgentMessage> SubscribeAsync(CancellationToken ct = default)
    {
        return SubscribeAsync(filter: null, ct);
    }

    public IAsyncEnumerable<AgentMessage> SubscribeAsync(string channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return SubscribeAsync(
            msg => string.Equals(msg.Channel, channel, StringComparison.OrdinalIgnoreCase),
            ct
        );
    }

    public async IAsyncEnumerable<AgentMessage> SubscribeAsync(
        Func<AgentMessage, bool>? filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var subId = Guid.NewGuid();
        var options = new BoundedChannelOptions(_subscriberChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        };
        var channel = Channel.CreateBounded<AgentMessage>(options);
        var sub = new Subscription(subId, channel, filter);

        _subscriptions[subId] = sub;

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var msg))
                {
                    yield return msg;
                }
            }
        }
        finally
        {
            _subscriptions.TryRemove(subId, out _);
            channel.Writer.TryComplete();
        }
    }

    public IReadOnlyList<AgentMessage> GetRecentHistory(int maxCount = 50)
    {
        var array = _recentHistory.ToArray();
        if (array.Length <= maxCount)
        {
            return array;
        }

        var result = new AgentMessage[maxCount];
        Array.Copy(array, array.Length - maxCount, result, 0, maxCount);
        return result;
    }
}
