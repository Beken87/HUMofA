namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Supported communication channel adapter types.
/// </summary>
public enum ChannelType
{
    Telegram,
    Discord,
    Slack,
    WhatsApp,
    Signal,
    Email,
    Cli,
    Mock
}

/// <summary>
/// Standard contract for bidirectional gateway communication channels.
/// </summary>
public interface IChannelAdapter
{
    /// <summary>
    /// Unique identifier for this adapter instance.
    /// </summary>
    string ChannelId { get; }

    /// <summary>
    /// The channel protocol type.
    /// </summary>
    ChannelType Type { get; }

    /// <summary>
    /// Indicates whether the adapter is actively connected and ready for traffic.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// True when operating in mock/offline stub fallback mode (e.g. Missing .env credentials).
    /// </summary>
    bool IsMock { get; }

    /// <summary>
    /// Starts the channel adapter background listener or connection session.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the channel adapter.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Transmits an outbound message through this channel.
    /// </summary>
    Task<bool> SendMessageAsync(AgentMessage message, CancellationToken ct = default);

    /// <summary>
    /// Transmits an interactive smart approval card/prompt with approve/deny buttons.
    /// </summary>
    Task<bool> SendApprovalPromptAsync(ApprovalRequest request, CancellationToken ct = default);

    /// <summary>
    /// Simulates an incoming message from the remote channel into the platform.
    /// </summary>
    void SimulateInboundMessage(AgentMessage message);

    /// <summary>
    /// History of messages sent through this adapter (primarily used in mock/testing).
    /// </summary>
    IReadOnlyList<AgentMessage> SentMessages { get; }

    /// <summary>
    /// History of approval prompts sent through this adapter.
    /// </summary>
    IReadOnlyList<ApprovalRequest> SentApprovals { get; }
}
