using System.Runtime.CompilerServices;
using System.Text;

namespace HUMofA.Gateway;

/// <summary>
/// Represents a progressive partial text update emitted during ghost typing.
/// </summary>
public record GhostTypingUpdate(
    string PartialContent,
    char CurrentChar,
    int CharIndex,
    int TotalLength,
    bool IsCompleted
);

/// <summary>
/// Synchronizes inbound remote channel messages to the Desktop Host chat interface
/// as real-time character-by-character ghost typing before triggering agent reasoning.
/// </summary>
public class GhostTypingSync
{

    /// <summary>
    /// Default minimum inter-character delay in milliseconds (10ms).
    /// </summary>
    public int MinDelayMs { get; init; } = 10;

    /// <summary>
    /// Default maximum inter-character delay in milliseconds (30ms).
    /// </summary>
    public int MaxDelayMs { get; init; } = 30;

    /// <summary>
    /// Character length threshold beyond which ghost typing pacing dynamically accelerates
    /// to prevent lag on massive text payloads.
    /// </summary>
    public int AccelerationThresholdChars { get; init; } = 200;

    /// <summary>
    /// Streams character-by-character ghost typing events into the event bus,
    /// culminating in the finalized message that activates agent reasoning.
    /// </summary>
    public async Task StreamGhostTypingAsync(
        AgentMessage inboundMessage,
        IEventBus eventBus,
        TimeSpan? pacingDelay = null,
        string targetChannel = "desktop",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inboundMessage);
        ArgumentNullException.ThrowIfNull(eventBus);

        string content = inboundMessage.Content ?? string.Empty;
        if (content.Length == 0)
        {
            // Empty message directly publishes final message
            var emptyCompleted = AgentMessage.Create(
                targetChannel,
                inboundMessage.Sender,
                string.Empty,
                id: inboundMessage.Id,
                metadata: new Dictionary<string, string>
                {
                    ["sourceChannel"] = inboundMessage.Channel,
                    ["isFinal"] = "true",
                    ["ghostTypingComplete"] = "true"
                }
            );
            await eventBus.PublishAsync(emptyCompleted, ct).ConfigureAwait(false);
            return;
        }

        var sb = new StringBuilder(content.Length);
        int total = content.Length;

        // Dynamic acceleration factor if text is large
        double accelerationMultiplier = total > AccelerationThresholdChars
            ? Math.Max(0.1, (double)AccelerationThresholdChars / total)
            : 1.0;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            char c = content[i];
            sb.Append(c);

            var ghostMsg = AgentMessage.GhostTyping(
                targetChannel,
                inboundMessage.Sender,
                sb.ToString(),
                metadata: new Dictionary<string, string>
                {
                    ["sourceChannel"] = inboundMessage.Channel,
                    ["originalId"] = inboundMessage.Id,
                    ["charIndex"] = i.ToString(),
                    ["totalLength"] = total.ToString(),
                    ["currentChar"] = c.ToString(),
                    ["isFinal"] = "false"
                }
            );

            await eventBus.PublishAsync(ghostMsg, ct).ConfigureAwait(false);

            // Compute delay
            TimeSpan delay;
            if (pacingDelay.HasValue)
            {
                delay = pacingDelay.Value;
            }
            else
            {
                int baseMs = Random.Shared.Next(MinDelayMs, MaxDelayMs + 1);
                int acceleratedMs = (int)Math.Max(1, Math.Round(baseMs * accelerationMultiplier));
                delay = TimeSpan.FromMilliseconds(acceleratedMs);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        // Finalized message triggering autonomous agent reasoning
        var completedMsg = AgentMessage.Create(
            targetChannel,
            inboundMessage.Sender,
            content,
            id: inboundMessage.Id,
            metadata: new Dictionary<string, string>
            {
                ["sourceChannel"] = inboundMessage.Channel,
                ["originalId"] = inboundMessage.Id,
                ["isFinal"] = "true",
                ["ghostTypingComplete"] = "true",
                ["triggerAgentReasoning"] = "true"
            }
        );

        await eventBus.PublishAsync(completedMsg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Yields progressive ghost typing updates directly as an asynchronous stream.
    /// </summary>
    public async IAsyncEnumerable<GhostTypingUpdate> StreamUpdatesAsync(
        string content,
        TimeSpan? pacingDelay = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            yield return new GhostTypingUpdate(string.Empty, '\0', 0, 0, true);
            yield break;
        }

        var sb = new StringBuilder(content.Length);
        int total = content.Length;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            char c = content[i];
            sb.Append(c);
            bool isCompleted = (i == total - 1);

            yield return new GhostTypingUpdate(
                sb.ToString(),
                c,
                i,
                total,
                isCompleted
            );

            if (!isCompleted)
            {
                TimeSpan delay = pacingDelay ?? TimeSpan.FromMilliseconds(Random.Shared.Next(MinDelayMs, MaxDelayMs + 1));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
