using System.Collections.ObjectModel;

namespace HUMofA.Gateway;

/// <summary>
/// Immutable message contract exchanged over the CentralEventBus across all gateway channels.
/// </summary>
public record AgentMessage(
    string Id,
    string Channel,
    string Sender,
    string Content,
    DateTime Timestamp,
    bool IsGhostTyping = false,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>
    /// Creates a standard completed message.
    /// </summary>
    public static AgentMessage Create(
        string channel,
        string sender,
        string content,
        string? id = null,
        DateTime? timestamp = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AgentMessage(
            id ?? Guid.NewGuid().ToString("N"),
            channel,
            sender,
            content,
            timestamp ?? DateTime.UtcNow,
            IsGhostTyping: false,
            Metadata: metadata ?? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>())
        );
    }

    /// <summary>
    /// Creates a progressive partial ghost typing message.
    /// </summary>
    public static AgentMessage GhostTyping(
        string channel,
        string sender,
        string partialContent,
        string? id = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AgentMessage(
            id ?? Guid.NewGuid().ToString("N"),
            channel,
            sender,
            partialContent,
            DateTime.UtcNow,
            IsGhostTyping: true,
            Metadata: metadata ?? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>())
        );
    }
}
