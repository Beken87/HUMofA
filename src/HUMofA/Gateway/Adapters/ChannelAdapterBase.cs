using System.Collections.Concurrent;
using System.Text.Json;

namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Abstract base class providing credential inspection, mock fallback management,
/// sent message logging, outbound payload serialization, and bidirectional routing to the CentralEventBus.
/// </summary>
public abstract class ChannelAdapterBase : IChannelAdapter
{
    private readonly ConcurrentBag<AgentMessage> _sentMessages = new();
    private readonly ConcurrentBag<ApprovalRequest> _sentApprovals = new();
    protected readonly IEventBus? _eventBus;
    protected readonly GhostTypingSync? _ghostTypingSync;
    protected readonly HttpClient? _httpClient;

    public string ChannelId { get; }
    public ChannelType Type { get; }
    public bool IsConnected { get; protected set; }
    public bool IsMock { get; protected set; }

    /// <summary>
    /// Gets the most recent outbound payload object prepared by this adapter.
    /// </summary>
    public object? LastOutboundPayload { get; protected set; }

    /// <summary>
    /// Gets the JSON serialization of the most recent outbound payload prepared by this adapter.
    /// </summary>
    public string? LastOutboundPayloadJson { get; protected set; }

    public IReadOnlyList<AgentMessage> SentMessages => _sentMessages.ToList();
    public IReadOnlyList<ApprovalRequest> SentApprovals => _sentApprovals.ToList();

    protected ChannelAdapterBase(
        string channelId,
        ChannelType type,
        IEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null,
        HttpClient? httpClient = null)
    {
        ChannelId = channelId;
        Type = type;
        _eventBus = eventBus;
        _ghostTypingSync = ghostTypingSync;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Builds a channel-specific payload model for the given message.
    /// </summary>
    protected virtual object? BuildMessagePayload(AgentMessage message) => null;

    /// <summary>
    /// Builds a channel-specific payload model for the given approval request.
    /// </summary>
    protected virtual object? BuildApprovalPayload(ApprovalRequest request) => null;

    /// <summary>
    /// Checks environment variables and determines if required credentials exist.
    /// </summary>
    protected abstract bool VerifyCredentials();

    public virtual Task StartAsync(CancellationToken ct = default)
    {
        bool hasCredentials = VerifyCredentials();
        if (!hasCredentials)
        {
            IsMock = true;
            Console.WriteLine($"[{ChannelId}] Credentials missing or offline. Defaulting gracefully to Mock/Stub mode.");
        }
        else
        {
            IsMock = false;
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public virtual Task StopAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public virtual Task<bool> SendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _sentMessages.Add(message);

        var payload = BuildMessagePayload(message);
        if (payload != null)
        {
            LastOutboundPayload = payload;
            LastOutboundPayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        if (IsMock)
        {
            // In mock mode, log and record without hitting live network
            return Task.FromResult(true);
        }

        return SendLiveMessageAsync(message, ct);
    }

    public virtual Task<bool> SendApprovalPromptAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _sentApprovals.Add(request);

        var payload = BuildApprovalPayload(request);
        if (payload != null)
        {
            LastOutboundPayload = payload;
            LastOutboundPayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        if (IsMock)
        {
            return Task.FromResult(true);
        }

        return SendLiveApprovalPromptAsync(request, ct);
    }

    /// <summary>
    /// Simulates incoming traffic from the remote channel into the platform.
    /// Emits to CentralEventBus and optionally initiates GhostTypingSync.
    /// </summary>
    public virtual void SimulateInboundMessage(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_eventBus == null)
        {
            return;
        }

        // Fire-and-forget publish to event bus
        Task.Run(async () =>
        {
            if (_ghostTypingSync != null)
            {
                await _ghostTypingSync.StreamGhostTypingAsync(
                    message,
                    _eventBus,
                    pacingDelay: TimeSpan.FromMilliseconds(5)
                ).ConfigureAwait(false);
            }
            else
            {
                await _eventBus.PublishAsync(message).ConfigureAwait(false);
            }
        });
    }

    protected virtual Task<bool> SendLiveMessageAsync(AgentMessage message, CancellationToken ct) =>
        Task.FromResult(true);

    protected virtual Task<bool> SendLiveApprovalPromptAsync(ApprovalRequest request, CancellationToken ct) =>
        Task.FromResult(true);
}
