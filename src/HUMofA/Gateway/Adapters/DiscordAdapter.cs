using System.Text;
using System.Text.Json;

namespace HUMofA.Gateway.Adapters;

/// <summary>
/// Channel adapter for Discord Gateway v10 / REST API.
/// Supports embeds, interactive ButtonComponents for approvals, and offline stub fallback.
/// </summary>
public class DiscordAdapter : ChannelAdapterBase
{
    public const string DefaultChannelId = "discord";

    public DiscordAdapter(
        IEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null,
        string channelId = DefaultChannelId,
        HttpClient? httpClient = null)
        : base(channelId, ChannelType.Discord, eventBus, ghostTypingSync, httpClient)
    {
    }

    protected override bool VerifyCredentials()
    {
        string? token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        string? channelId = Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID");
        return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(channelId);
    }

    protected override object? BuildMessagePayload(AgentMessage message)
    {
        return new DiscordMessagePayload(
            Content: $"[**{message.Sender}**]: {message.Content}"
        );
    }

    protected override object? BuildApprovalPayload(ApprovalRequest request)
    {
        int color = request.Tier switch
        {
            RiskTier.Green => 0x2ECC71,  // Emerald Green
            RiskTier.Yellow => 0xF1C40F, // Amber Yellow
            RiskTier.Red => 0xE74C3C,    // Crimson Red
            _ => 0x95A5A6
        };

        var fields = new List<DiscordEmbedField>
        {
            new("Action", request.ActionName, Inline: true),
            new("Tier", request.Tier.ToString(), Inline: true),
            new("Request ID", request.Id, Inline: false)
        };

        if (request.Parameters is { Count: > 0 })
        {
            string paramStr = string.Join(", ", request.Parameters.Select(kv => $"{kv.Key}={kv.Value}"));
            fields.Add(new("Parameters", paramStr, Inline: false));
        }

        var embed = new DiscordEmbed(
            Title: "⚠️ HUMofA Action Approval Required",
            Description: request.Description,
            Color: color,
            Fields: fields,
            Footer: new DiscordEmbedFooter("HUMofA Autonomous Security Gateway"),
            Timestamp: DateTime.UtcNow.ToString("O")
        );

        var actionRow = new DiscordActionRow(
            Type: 1,
            Components: new[]
            {
                new DiscordButton(Type: 2, Style: 3, Label: "Approve", CustomId: $"discord_appr_{request.Id}_approve"),
                new DiscordButton(Type: 2, Style: 4, Label: "Deny", CustomId: $"discord_appr_{request.Id}_deny")
            }
        );

        return new DiscordMessagePayload(
            Content: null,
            Embeds: new[] { embed },
            Components: new[] { actionRow }
        );
    }

    protected override async Task<bool> SendLiveMessageAsync(AgentMessage message, CancellationToken ct)
    {
        var payload = (DiscordMessagePayload)(LastOutboundPayload ?? BuildMessagePayload(message)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? "mock_token";
        string channelId = Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID") ?? "default_channel";
        string url = $"https://discord.com/api/v10/channels/{channelId}/messages";

        if (_httpClient != null)
        {
            using var requestMsg = new HttpRequestMessage(HttpMethod.Post, url);
            requestMsg.Headers.TryAddWithoutValidation("Authorization", $"Bot {token}");
            requestMsg.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(requestMsg, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        using var simRequest = new HttpRequestMessage(HttpMethod.Post, url);
        simRequest.Headers.TryAddWithoutValidation("Authorization", $"Bot {token}");
        simRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return simRequest.Content != null && !string.IsNullOrEmpty(payload.Content);
    }

    protected override async Task<bool> SendLiveApprovalPromptAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = (DiscordMessagePayload)(LastOutboundPayload ?? BuildApprovalPayload(request)!);
        string json = LastOutboundPayloadJson ?? JsonSerializer.Serialize(payload);

        string token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? "mock_token";
        string channelId = Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID") ?? "default_channel";
        string url = $"https://discord.com/api/v10/channels/{channelId}/messages";

        if (_httpClient != null)
        {
            using var requestMsg = new HttpRequestMessage(HttpMethod.Post, url);
            requestMsg.Headers.TryAddWithoutValidation("Authorization", $"Bot {token}");
            requestMsg.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(requestMsg, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        using var simRequest = new HttpRequestMessage(HttpMethod.Post, url);
        simRequest.Headers.TryAddWithoutValidation("Authorization", $"Bot {token}");
        simRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return simRequest.Content != null && payload.Components is { Count: > 0 };
    }
}
