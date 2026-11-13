using System.Text;
using System.Text.Json;

namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Channel adapter for Slack Web API and Socket Mode.
/// Supports Block Kit interactive cards for approvals and offline stub fallback.
/// </summary>
public class SlackAdapter : ChannelAdapterBase
{
    public const string DefaultChannelId = "slack";

    public SlackAdapter(
        IEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null,
        string channelId = DefaultChannelId,
        HttpClient? httpClient = null)
        : base(channelId, ChannelType.Slack, eventBus, ghostTypingSync, httpClient)
    {
    }

    protected override bool VerifyCredentials()
    {
        string? token = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN");
        string? channelId = Environment.GetEnvironmentVariable("SLACK_CHANNEL_ID");
        return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(channelId);
    }

    protected override object? BuildMessagePayload(AgentMessage message)
    {
        string channel = Environment.GetEnvironmentVariable("SLACK_CHANNEL_ID") ?? "default_channel";
        var section = new SlackSectionBlock(
            Text: new SlackTextObject(Type: "mrkdwn", Text: $"*[{message.Sender}]*: {message.Content}")
        );

        return new SlackMessagePayload(
            Channel: channel,
            Text: message.Content,
            Blocks: new object[] { section }
        );
    }

    protected override object? BuildApprovalPayload(ApprovalRequest request)
    {
        string channel = Environment.GetEnvironmentVariable("SLACK_CHANNEL_ID") ?? "default_channel";
        var section = new SlackSectionBlock(
            Text: new SlackTextObject(
                Type: "mrkdwn",
                Text: $"⚠️ *Approval Required*: `{request.ActionName}`\n*Tier*: {request.Tier}\n*ID*: `{request.Id}`\n{request.Description}"
            )
        );

        var actions = new SlackActionsBlock(
            BlockId: $"appr_block_{request.Id}",
            Elements: new[]
            {
                new SlackButtonElement(
                    Text: new SlackTextObject(Type: "plain_text", Text: "Approve", Emoji: true),
                    ActionId: $"slack_appr_{request.Id}_approve",
                    Value: "approve",
                    Style: "primary"
                ),
                new SlackButtonElement(
                    Text: new SlackTextObject(Type: "plain_text", Text: "Deny", Emoji: true),
                    ActionId: $"slack_appr_{request.Id}_deny",
                    Value: "deny",
                    Style: "danger"
                )
            }
        );

        return new SlackMessagePayload(
            Channel: channel,
            Text: $"Approval required: {request.ActionName}",
            Blocks: new object[] { section, actions }
        );
    }

    protected override async Task<bool> SendLiveMessageAsync(AgentMessage message, CancellationToken ct)
    {
        var payload = (SlackMessagePayload)(LastOutboundPayload ?? BuildMessagePayload(message)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string token = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN") ?? "mock_token";
        string url = "https://slack.com/api/chat.postMessage";

        if (_httpClient != null)
        {
            using var requestMsg = new HttpRequestMessage(HttpMethod.Post, url);
            requestMsg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            requestMsg.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(requestMsg, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        using var simRequest = new HttpRequestMessage(HttpMethod.Post, url);
        simRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        simRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return simRequest.Content != null && !string.IsNullOrEmpty(payload.Text);
    }

    protected override async Task<bool> SendLiveApprovalPromptAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = (SlackMessagePayload)(LastOutboundPayload ?? BuildApprovalPayload(request)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string token = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN") ?? "mock_token";
        string url = "https://slack.com/api/chat.postMessage";

        if (_httpClient != null)
        {
            using var requestMsg = new HttpRequestMessage(HttpMethod.Post, url);
            requestMsg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            requestMsg.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(requestMsg, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        using var simRequest = new HttpRequestMessage(HttpMethod.Post, url);
        simRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        simRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return simRequest.Content != null && payload.Blocks is { Count: > 0 };
    }
}
