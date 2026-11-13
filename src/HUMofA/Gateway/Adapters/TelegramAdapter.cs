using System.Text;
using System.Text.Json;

namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Channel adapter for Telegram Bot API v7.0+.
/// Supports markdown formatting, inline approval keyboard callbacks, and offline stub fallback.
/// </summary>
public class TelegramAdapter : ChannelAdapterBase
{
    public const string DefaultChannelId = "telegram";

    public TelegramAdapter(
        IEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null,
        string channelId = DefaultChannelId,
        HttpClient? httpClient = null)
        : base(channelId, ChannelType.Telegram, eventBus, ghostTypingSync, httpClient)
    {
    }

    protected override bool VerifyCredentials()
    {
        string? token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        string? chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ??
                         Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_CHAT_IDS");
        return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(chatId);
    }

    protected override object? BuildMessagePayload(AgentMessage message)
    {
        string chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ??
                        Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_CHAT_IDS") ??
                        "default_chat";

        return new TelegramMessagePayload(
            ChatId: chatId,
            Text: message.Content,
            ParseMode: "MarkdownV2"
        );
    }

    protected override object? BuildApprovalPayload(ApprovalRequest request)
    {
        string chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ??
                        Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_CHAT_IDS") ??
                        "default_chat";

        var keyboard = new TelegramInlineKeyboard(new[]
        {
            new[]
            {
                new TelegramInlineKeyboardButton("✅ Approve", CallbackData: $"appr:{request.Id}:approve"),
                new TelegramInlineKeyboardButton("❌ Deny", CallbackData: $"appr:{request.Id}:deny")
            }
        });

        string text = $"⚠️ *HUMofA Approval Required*\nAction: `{request.ActionName}`\nTier: *{request.Tier}*\nID: `{request.Id}`\n\n{request.Description}";

        return new TelegramMessagePayload(
            ChatId: chatId,
            Text: text,
            ParseMode: "MarkdownV2",
            ReplyMarkup: keyboard
        );
    }

    protected override async Task<bool> SendLiveMessageAsync(AgentMessage message, CancellationToken ct)
    {
        var payload = (TelegramMessagePayload)(LastOutboundPayload ?? BuildMessagePayload(message)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "mock_token";
        string url = $"https://api.telegram.org/bot{token}/sendMessage";

        if (_httpClient != null)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return requestMessage.Content != null && !string.IsNullOrEmpty(payload.Text);
    }

    protected override async Task<bool> SendLiveApprovalPromptAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = (TelegramMessagePayload)(LastOutboundPayload ?? BuildApprovalPayload(request)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "mock_token";
        string url = $"https://api.telegram.org/bot{token}/sendMessage";

        if (_httpClient != null)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return requestMessage.Content != null && payload.ReplyMarkup != null;
    }
}
