namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Dedicated mock channel adapter for automated testing and zero-credential offline environments.
/// </summary>
public class MockChannelAdapter : ChannelAdapterBase
{
    public const string DefaultChannelId = "mock";
    public bool ShouldFailSend { get; set; }
    public bool AutoApproveOnPrompt { get; set; }
    public SmartApproval? AttachedSmartApproval { get; set; }

    public MockChannelAdapter(
        string channelId = DefaultChannelId,
        IEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null)
        : base(channelId, ChannelType.Mock, eventBus, ghostTypingSync)
    {
    }

    protected override bool VerifyCredentials() => false; // Always defaults to mock

    public override Task StartAsync(CancellationToken ct = default)
    {
        IsMock = true;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public override Task<bool> SendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        if (ShouldFailSend)
        {
            return Task.FromResult(false);
        }

        return base.SendMessageAsync(message, ct);
    }

    public override Task<bool> SendApprovalPromptAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        base.SendApprovalPromptAsync(request, ct);

        if (AutoApproveOnPrompt && AttachedSmartApproval != null)
        {
            AttachedSmartApproval.SubmitApproval(request.Id, approved: true, reason: "Mock Auto-Approved", approvedBy: "mock:auto");
        }

        return Task.FromResult(true);
    }
}
