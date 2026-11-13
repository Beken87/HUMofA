namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Channel adapter for interactive Command Line Interface (CLI) input/output.
/// Supports ANSI console formatting and offline test simulation.
/// </summary>
public class CliAdapter : ChannelAdapterBase
{
    public const string DefaultChannelId = "cli";
    private readonly TextWriter? _outputWriter;
    private readonly TextReader? _inputReader;

    public CliAdapter(
        IEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null,
        string channelId = DefaultChannelId,
        TextWriter? outputWriter = null,
        TextReader? inputReader = null)
        : base(channelId, ChannelType.Cli, eventBus, ghostTypingSync)
    {
        _outputWriter = outputWriter;
        _inputReader = inputReader;
    }

    protected override bool VerifyCredentials()
    {
        string? cliEnabled = Environment.GetEnvironmentVariable("CLI_ENABLED");
        if (string.Equals(cliEnabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // CLI is always available if console is interactive or explicit writers are provided
        return _outputWriter != null || !Console.IsInputRedirected;
    }

    protected override object? BuildMessagePayload(AgentMessage message)
    {
        return new CliMessagePayload(
            Channel: ChannelId,
            Sender: message.Sender,
            Content: message.Content,
            Timestamp: message.Timestamp
        );
    }

    protected override object? BuildApprovalPayload(ApprovalRequest request)
    {
        return new CliApprovalPayload(
            RequestId: request.Id,
            ActionName: request.ActionName,
            Tier: request.Tier,
            Description: request.Description,
            Parameters: request.Parameters
        );
    }

    protected override Task<bool> SendLiveMessageAsync(AgentMessage message, CancellationToken ct)
    {
        var writer = _outputWriter ?? Console.Out;
        writer.WriteLine($"[CLI Outbound -> {message.Sender}]: {message.Content}");
        return Task.FromResult(true);
    }

    protected override Task<bool> SendLiveApprovalPromptAsync(ApprovalRequest request, CancellationToken ct)
    {
        var writer = _outputWriter ?? Console.Out;
        writer.WriteLine($"[CLI Approval Required] ID: {request.Id} | Tier: {request.Tier} | Action: {request.ActionName}");
        writer.WriteLine($"Description: {request.Description}");
        return Task.FromResult(true);
    }
}
