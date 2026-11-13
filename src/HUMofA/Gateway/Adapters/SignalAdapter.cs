using System.Text;
using System.Text.Json;

namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Channel adapter for Signal CLI JSON-RPC daemon / REST API.
/// Supports secure end-to-end messaging and offline stub fallback.
/// </summary>
public class SignalAdapter : ChannelAdapterBase
{
    public const string DefaultChannelId = "signal";

    public SignalAdapter(
        IEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null,
        string channelId = DefaultChannelId,
        HttpClient? httpClient = null)
        : base(channelId, ChannelType.Signal, eventBus, ghostTypingSync, httpClient)
    {
    }

    protected override bool VerifyCredentials()
    {
        string? apiUrl = Environment.GetEnvironmentVariable("SIGNAL_CLI_ENDPOINT") ??
                         Environment.GetEnvironmentVariable("SIGNAL_API_URL");
        string? phoneNumber = Environment.GetEnvironmentVariable("SIGNAL_PHONE_NUMBER");
        return !string.IsNullOrWhiteSpace(apiUrl) || !string.IsNullOrWhiteSpace(phoneNumber);
    }

    protected override object? BuildMessagePayload(AgentMessage message)
    {
        string recipient = message.Metadata?.GetValueOrDefault("recipient") ??
                           Environment.GetEnvironmentVariable("SIGNAL_PHONE_NUMBER") ??
                           "default_recipient";

        return new SignalJsonRpcRequest(
            Method: "send",
            Params: new SignalSendParams(
                Recipients: new[] { recipient },
                Message: $"[{message.Sender}]: {message.Content}"
            ),
            Id: Guid.NewGuid().ToString("N")
        );
    }

    protected override object? BuildApprovalPayload(ApprovalRequest request)
    {
        string recipient = request.Parameters?.GetValueOrDefault("recipient") ??
                           Environment.GetEnvironmentVariable("SIGNAL_PHONE_NUMBER") ??
                           "default_recipient";

        string promptText = $"⚠️ HUMofA APPROVAL REQUIRED\nAction: {request.ActionName} [{request.Tier}]\nID: {request.Id}\n{request.Description}\n\nReply 'APPROVE {request.Id}' or 'DENY {request.Id}'";

        return new SignalJsonRpcRequest(
            Method: "send",
            Params: new SignalSendParams(
                Recipients: new[] { recipient },
                Message: promptText
            ),
            Id: Guid.NewGuid().ToString("N")
        );
    }

    protected override async Task<bool> SendLiveMessageAsync(AgentMessage message, CancellationToken ct)
    {
        var payload = (SignalJsonRpcRequest)(LastOutboundPayload ?? BuildMessagePayload(message)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string endpoint = Environment.GetEnvironmentVariable("SIGNAL_CLI_ENDPOINT") ??
                          Environment.GetEnvironmentVariable("SIGNAL_API_URL") ??
                          "http://127.0.0.1:8080/api/v1/rpc";

        if (_httpClient != null)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(endpoint, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        using var simRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return simRequest.Content != null && !string.IsNullOrEmpty(payload.Params?.Message);
    }

    protected override async Task<bool> SendLiveApprovalPromptAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = (SignalJsonRpcRequest)(LastOutboundPayload ?? BuildApprovalPayload(request)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string endpoint = Environment.GetEnvironmentVariable("SIGNAL_CLI_ENDPOINT") ??
                          Environment.GetEnvironmentVariable("SIGNAL_API_URL") ??
                          "http://127.0.0.1:8080/api/v1/rpc";

        if (_httpClient != null)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(endpoint, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        using var simRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return simRequest.Content != null && payload.Params?.Recipients?.Count > 0;
    }
}
