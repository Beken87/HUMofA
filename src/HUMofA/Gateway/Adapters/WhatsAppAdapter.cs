using System.Text;
using System.Text.Json;

namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Channel adapter for WhatsApp Business Cloud API / Baileys bridge.
/// Supports interactive button templates for approvals and offline stub fallback.
/// </summary>
public class WhatsAppAdapter : ChannelAdapterBase
{
    public const string DefaultChannelId = "whatsapp";

    public WhatsAppAdapter(
        IEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null,
        string channelId = DefaultChannelId,
        HttpClient? httpClient = null)
        : base(channelId, ChannelType.WhatsApp, eventBus, ghostTypingSync, httpClient)
    {
    }

    protected override bool VerifyCredentials()
    {
        string? apiKey = Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") ??
                         Environment.GetEnvironmentVariable("WHATSAPP_API_KEY");
        string? phoneId = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID");
        return !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(phoneId);
    }

    protected override object? BuildMessagePayload(AgentMessage message)
    {
        string to = message.Metadata?.GetValueOrDefault("recipient") ??
                    Environment.GetEnvironmentVariable("WHATSAPP_RECIPIENT_PHONE") ??
                    "default_recipient";

        return new WhatsAppTextMessagePayload(
            To: to,
            Text: new WhatsAppTextObject(message.Content)
        );
    }

    protected override object? BuildApprovalPayload(ApprovalRequest request)
    {
        string to = request.Parameters?.GetValueOrDefault("recipient") ??
                    Environment.GetEnvironmentVariable("WHATSAPP_RECIPIENT_PHONE") ??
                    "default_recipient";

        var buttons = new[]
        {
            new WhatsAppButton(Reply: new WhatsAppButtonReply($"wa_appr_{request.Id}_yes", "Approve")),
            new WhatsAppButton(Reply: new WhatsAppButtonReply($"wa_appr_{request.Id}_no", "Deny"))
        };

        var interactive = new WhatsAppInteractiveObject(
            Header: new WhatsAppHeaderObject("text", "⚠️ Approval Required"),
            Body: new WhatsAppTextObject($"Action: {request.ActionName} [{request.Tier}]\nID: {request.Id}\n\n{request.Description}"),
            Footer: new WhatsAppTextObject("HUMofA Autonomous Security"),
            Action: new WhatsAppButtonAction(buttons)
        );

        return new WhatsAppInteractivePayload(
            To: to,
            Interactive: interactive
        );
    }

    protected override async Task<bool> SendLiveMessageAsync(AgentMessage message, CancellationToken ct)
    {
        var payload = (WhatsAppTextMessagePayload)(LastOutboundPayload ?? BuildMessagePayload(message)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string token = Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") ??
                       Environment.GetEnvironmentVariable("WHATSAPP_API_KEY") ??
                       "mock_token";
        string phoneId = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID") ?? "default_phone_id";
        string url = $"https://graph.facebook.com/v19.0/{phoneId}/messages";

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
        return simRequest.Content != null && !string.IsNullOrEmpty(payload.Text?.Text);
    }

    protected override async Task<bool> SendLiveApprovalPromptAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = (WhatsAppInteractivePayload)(LastOutboundPayload ?? BuildApprovalPayload(request)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string token = Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") ??
                       Environment.GetEnvironmentVariable("WHATSAPP_API_KEY") ??
                       "mock_token";
        string phoneId = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID") ?? "default_phone_id";
        string url = $"https://graph.facebook.com/v19.0/{phoneId}/messages";

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
        return simRequest.Content != null && payload.Interactive?.Action?.Buttons?.Count > 0;
    }
}
