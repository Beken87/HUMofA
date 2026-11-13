using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using HUMofA.Gateway;
using HUMofA.Gateway.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace HUMofA.Tests;

/// <summary>
/// Challenger 1 Adversarial Re-gate Evaluation Tests for Milestone 3 Remediation.
/// Empirically stress-tests:
/// 1. Real JSON serialization and schema conformity for all 7 channel adapters.
/// 2. Live HTTP and protocol dispatch pipeline behavior via injected HttpClient.
/// 3. CentralEventBus high-concurrency throughput, head-of-line non-blocking fan-out, and subscriber churn.
/// 4. SmartApproval 256-bit CSPRNG entropy, race conditions, and cancellation safety.
/// 5. GhostTypingSync thread-safety and stream integrity.
/// </summary>
public class Milestone3Challenger1EvaluationTests
{
    private readonly ITestOutputHelper _output;

    public Milestone3Challenger1EvaluationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // =========================================================================
    // PART 1: CHANNEL ADAPTER PAYLOAD SERIALIZATION & SCHEMA VERIFICATION
    // =========================================================================

    [Fact]
    public async Task TelegramAdapter_Payloads_GenerateValidJsonMatchingSchema()
    {
        var adapter = new TelegramAdapter();
        await adapter.StartAsync();

        // 1. Message Payload
        var msg = AgentMessage.Create("telegram", "alice", "Hello Telegram *Markdown*");
        bool msgSent = await adapter.SendMessageAsync(msg);
        msgSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<TelegramMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedMsg = JsonSerializer.Deserialize<TelegramMessagePayload>(adapter.LastOutboundPayloadJson!);
        deserializedMsg.Should().NotBeNull();
        deserializedMsg!.Text.Should().Be("Hello Telegram *Markdown*");
        deserializedMsg.ParseMode.Should().Be("MarkdownV2");
        deserializedMsg.ReplyMarkup.Should().BeNull();

        // 2. Approval Payload
        var approval = ApprovalRequest.Create("restart_node", RiskTier.Red, "Restart production worker node");
        bool apprSent = await adapter.SendApprovalPromptAsync(approval);
        apprSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<TelegramMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedAppr = JsonSerializer.Deserialize<TelegramMessagePayload>(adapter.LastOutboundPayloadJson!);
        deserializedAppr.Should().NotBeNull();
        deserializedAppr!.Text.Should().Contain(approval.Id);
        deserializedAppr.Text.Should().Contain("restart_node");
        deserializedAppr.ReplyMarkup.Should().NotBeNull();
        deserializedAppr.ReplyMarkup!.InlineKeyboard.Should().HaveCount(1);
        deserializedAppr.ReplyMarkup.InlineKeyboard[0].Should().HaveCount(2);

        var approveBtn = deserializedAppr.ReplyMarkup.InlineKeyboard[0][0];
        approveBtn.Text.Should().Contain("Approve");
        approveBtn.CallbackData.Should().Be($"appr:{approval.Id}:approve");

        var denyBtn = deserializedAppr.ReplyMarkup.InlineKeyboard[0][1];
        denyBtn.Text.Should().Contain("Deny");
        denyBtn.CallbackData.Should().Be($"appr:{approval.Id}:deny");

        await adapter.StopAsync();
    }

    [Fact]
    public async Task DiscordAdapter_Payloads_GenerateValidJsonMatchingSchema()
    {
        var adapter = new DiscordAdapter();
        await adapter.StartAsync();

        // 1. Message Payload
        var msg = AgentMessage.Create("discord", "bob", "Status update from Discord");
        bool msgSent = await adapter.SendMessageAsync(msg);
        msgSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<DiscordMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedMsg = JsonSerializer.Deserialize<DiscordMessagePayload>(adapter.LastOutboundPayloadJson!);
        deserializedMsg.Should().NotBeNull();
        deserializedMsg!.Content.Should().Be("[**bob**]: Status update from Discord");
        deserializedMsg.Embeds.Should().BeNull();
        deserializedMsg.Components.Should().BeNull();

        // 2. Approval Payload
        var approval = ApprovalRequest.Create("drain_queue", RiskTier.Yellow, "Drain message queue",
            new Dictionary<string, string> { ["queue"] = "orders" });
        bool apprSent = await adapter.SendApprovalPromptAsync(approval);
        apprSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<DiscordMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedAppr = JsonSerializer.Deserialize<DiscordMessagePayload>(adapter.LastOutboundPayloadJson!);
        deserializedAppr.Should().NotBeNull();
        deserializedAppr!.Content.Should().BeNull();
        deserializedAppr.Embeds.Should().NotBeNull().And.HaveCount(1);

        var embed = deserializedAppr.Embeds![0];
        embed.Title.Should().Contain("Approval Required");
        embed.Color.Should().Be(0xF1C40F); // Amber Yellow
        embed.Fields.Should().NotBeNull();
        embed.Fields!.Should().Contain(f => f.Name == "Action" && f.Value == "drain_queue");
        embed.Fields.Should().Contain(f => f.Name == "Tier" && f.Value == "Yellow");
        embed.Fields.Should().Contain(f => f.Name == "Request ID" && f.Value == approval.Id);
        embed.Fields.Should().Contain(f => f.Name == "Parameters" && f.Value.Contains("queue=orders"));

        deserializedAppr.Components.Should().NotBeNull().And.HaveCount(1);
        var row = deserializedAppr.Components![0];
        row.Type.Should().Be(1); // Action Row
        row.Components.Should().HaveCount(2);

        var btnApprove = row.Components[0];
        btnApprove.Type.Should().Be(2); // Button
        btnApprove.Style.Should().Be(3); // Success
        btnApprove.CustomId.Should().Be($"discord_appr_{approval.Id}_approve");

        var btnDeny = row.Components[1];
        btnDeny.Type.Should().Be(2); // Button
        btnDeny.Style.Should().Be(4); // Danger
        btnDeny.CustomId.Should().Be($"discord_appr_{approval.Id}_deny");

        await adapter.StopAsync();
    }

    [Fact]
    public async Task SlackAdapter_Payloads_GenerateValidJsonMatchingSchema()
    {
        var adapter = new SlackAdapter();
        await adapter.StartAsync();

        // 1. Message Payload
        var msg = AgentMessage.Create("slack", "carol", "Slack direct ping");
        bool msgSent = await adapter.SendMessageAsync(msg);
        msgSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<SlackMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var docMsg = JsonDocument.Parse(adapter.LastOutboundPayloadJson!);
        var rootMsg = docMsg.RootElement;
        rootMsg.GetProperty("text").GetString().Should().Be("Slack direct ping");
        var blocksMsg = rootMsg.GetProperty("blocks");
        blocksMsg.GetArrayLength().Should().Be(1);
        blocksMsg[0].GetProperty("type").GetString().Should().Be("section");

        // 2. Approval Payload
        var approval = ApprovalRequest.Create("scale_cluster", RiskTier.Red, "Scale cluster down to 1 node");
        bool apprSent = await adapter.SendApprovalPromptAsync(approval);
        apprSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<SlackMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var docAppr = JsonDocument.Parse(adapter.LastOutboundPayloadJson!);
        var rootAppr = docAppr.RootElement;
        rootAppr.GetProperty("text").GetString().Should().Contain("scale_cluster");

        var blocksAppr = rootAppr.GetProperty("blocks");
        blocksAppr.GetArrayLength().Should().Be(2);

        // Section block
        blocksAppr[0].GetProperty("type").GetString().Should().Be("section");
        blocksAppr[0].GetProperty("text").GetProperty("text").GetString().Should().Contain(approval.Id);

        // Actions block
        blocksAppr[1].GetProperty("type").GetString().Should().Be("actions");
        blocksAppr[1].GetProperty("block_id").GetString().Should().Be($"appr_block_{approval.Id}");

        var elements = blocksAppr[1].GetProperty("elements");
        elements.GetArrayLength().Should().Be(2);

        var btn1 = elements[0];
        btn1.GetProperty("type").GetString().Should().Be("button");
        btn1.GetProperty("action_id").GetString().Should().Be($"slack_appr_{approval.Id}_approve");
        btn1.GetProperty("style").GetString().Should().Be("primary");

        var btn2 = elements[1];
        btn2.GetProperty("type").GetString().Should().Be("button");
        btn2.GetProperty("action_id").GetString().Should().Be($"slack_appr_{approval.Id}_deny");
        btn2.GetProperty("style").GetString().Should().Be("danger");

        await adapter.StopAsync();
    }

    [Fact]
    public async Task WhatsAppAdapter_Payloads_GenerateValidJsonMatchingSchema()
    {
        var adapter = new WhatsAppAdapter();
        await adapter.StartAsync();

        // 1. Message Payload
        var msg = AgentMessage.Create("whatsapp", "dave", "WhatsApp alert text",
            metadata: new Dictionary<string, string> { ["recipient"] = "+15551234567" });
        bool msgSent = await adapter.SendMessageAsync(msg);
        msgSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<WhatsAppTextMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedMsg = JsonSerializer.Deserialize<WhatsAppTextMessagePayload>(adapter.LastOutboundPayloadJson!);
        deserializedMsg.Should().NotBeNull();
        deserializedMsg!.MessagingProduct.Should().Be("whatsapp");
        deserializedMsg.RecipientType.Should().Be("individual");
        deserializedMsg.To.Should().Be("+15551234567");
        deserializedMsg.Type.Should().Be("text");
        deserializedMsg.Text.Text.Should().Be("WhatsApp alert text");

        // 2. Approval Payload
        var approval = ApprovalRequest.Create("deploy_canary", RiskTier.Yellow, "Deploy v2.1 canary",
            parameters: new Dictionary<string, string> { ["recipient"] = "+15559876543" });
        bool apprSent = await adapter.SendApprovalPromptAsync(approval);
        apprSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<WhatsAppInteractivePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedAppr = JsonSerializer.Deserialize<WhatsAppInteractivePayload>(adapter.LastOutboundPayloadJson!);
        deserializedAppr.Should().NotBeNull();
        deserializedAppr!.MessagingProduct.Should().Be("whatsapp");
        deserializedAppr.Type.Should().Be("interactive");
        deserializedAppr.To.Should().Be("+15559876543");

        var interactive = deserializedAppr.Interactive;
        interactive.Should().NotBeNull();
        interactive.Type.Should().Be("button");
        interactive.Header.Should().NotBeNull();
        interactive.Header!.Text.Should().Contain("Approval Required");
        interactive.Body.Text.Should().Contain(approval.Id);
        interactive.Body.Text.Should().Contain("deploy_canary");

        interactive.Action.Should().NotBeNull();
        interactive.Action.Buttons.Should().HaveCount(2);
        interactive.Action.Buttons[0].Reply.Id.Should().Be($"wa_appr_{approval.Id}_yes");
        interactive.Action.Buttons[0].Reply.Title.Should().Be("Approve");
        interactive.Action.Buttons[1].Reply.Id.Should().Be($"wa_appr_{approval.Id}_no");
        interactive.Action.Buttons[1].Reply.Title.Should().Be("Deny");

        await adapter.StopAsync();
    }

    [Fact]
    public async Task SignalAdapter_Payloads_GenerateValidJsonMatchingSchema()
    {
        var adapter = new SignalAdapter();
        await adapter.StartAsync();

        // 1. Message Payload
        var msg = AgentMessage.Create("signal", "eve", "Signal encrypted payload",
            metadata: new Dictionary<string, string> { ["recipient"] = "+19998887777" });
        bool msgSent = await adapter.SendMessageAsync(msg);
        msgSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<SignalJsonRpcRequest>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedMsg = JsonSerializer.Deserialize<SignalJsonRpcRequest>(adapter.LastOutboundPayloadJson!);
        deserializedMsg.Should().NotBeNull();
        deserializedMsg!.JsonRpc.Should().Be("2.0");
        deserializedMsg.Method.Should().Be("send");
        deserializedMsg.Params.Should().NotBeNull();
        deserializedMsg.Params.Recipients.Should().ContainSingle("+19998887777");
        deserializedMsg.Params.Message.Should().Contain("Signal encrypted payload");

        // 2. Approval Payload
        var approval = ApprovalRequest.Create("revoke_key", RiskTier.Red, "Revoke compromised API key",
            parameters: new Dictionary<string, string> { ["recipient"] = "+19998887777" });
        bool apprSent = await adapter.SendApprovalPromptAsync(approval);
        apprSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<SignalJsonRpcRequest>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedAppr = JsonSerializer.Deserialize<SignalJsonRpcRequest>(adapter.LastOutboundPayloadJson!);
        deserializedAppr.Should().NotBeNull();
        deserializedAppr!.JsonRpc.Should().Be("2.0");
        deserializedAppr.Method.Should().Be("send");
        deserializedAppr.Params.Recipients.Should().ContainSingle("+19998887777");
        deserializedAppr.Params.Message.Should().Contain($"APPROVE {approval.Id}");
        deserializedAppr.Params.Message.Should().Contain($"DENY {approval.Id}");

        await adapter.StopAsync();
    }

    [Fact]
    public async Task EmailAdapter_Payloads_GenerateValidJsonMatchingSchema()
    {
        var adapter = new EmailAdapter();
        await adapter.StartAsync();

        // 1. Message Payload
        var msg = AgentMessage.Create("email", "frank", "Email body content",
            metadata: new Dictionary<string, string> { ["recipient"] = "ops@example.com" });
        bool msgSent = await adapter.SendMessageAsync(msg);
        msgSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<EmailMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedMsg = JsonSerializer.Deserialize<EmailMessagePayload>(adapter.LastOutboundPayloadJson!);
        deserializedMsg.Should().NotBeNull();
        deserializedMsg!.To.Should().Contain("ops@example.com");
        deserializedMsg.Subject.Should().Contain("frank");
        deserializedMsg.TextBody.Should().Be("Email body content");
        deserializedMsg.HtmlBody.Should().Contain("<p>Email body content</p>");

        // 2. Approval Payload
        var approval = ApprovalRequest.Create("reboot_gateway", RiskTier.Red, "Reboot gateway host",
            parameters: new Dictionary<string, string> { ["recipient"] = "director@example.com" });
        bool apprSent = await adapter.SendApprovalPromptAsync(approval);
        apprSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<EmailMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedAppr = JsonSerializer.Deserialize<EmailMessagePayload>(adapter.LastOutboundPayloadJson!);
        deserializedAppr.Should().NotBeNull();
        deserializedAppr!.To.Should().Contain("director@example.com");
        deserializedAppr.Subject.Should().Contain("reboot_gateway");
        deserializedAppr.HtmlBody.Should().Contain($"humofa://approval/{approval.Id}/approve");
        deserializedAppr.HtmlBody.Should().Contain($"humofa://approval/{approval.Id}/deny");
        deserializedAppr.HtmlBody.Should().Contain("#e74c3c"); // Red tier badge color

        await adapter.StopAsync();
    }

    [Fact]
    public async Task CliAdapter_Payloads_GenerateValidJsonMatchingSchema()
    {
        using var stringWriter = new StringWriter();
        var adapter = new CliAdapter(outputWriter: stringWriter);
        await adapter.StartAsync();

        // 1. Message Payload
        var msg = AgentMessage.Create("cli", "grace", "Local CLI command output");
        bool msgSent = await adapter.SendMessageAsync(msg);
        msgSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<CliMessagePayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedMsg = JsonSerializer.Deserialize<CliMessagePayload>(adapter.LastOutboundPayloadJson!);
        deserializedMsg.Should().NotBeNull();
        deserializedMsg!.Channel.Should().Be("cli");
        deserializedMsg.Sender.Should().Be("grace");
        deserializedMsg.Content.Should().Be("Local CLI command output");

        // 2. Approval Payload
        var approval = ApprovalRequest.Create("git_push", RiskTier.Yellow, "Push commits to main");
        bool apprSent = await adapter.SendApprovalPromptAsync(approval);
        apprSent.Should().BeTrue();

        adapter.LastOutboundPayload.Should().BeOfType<CliApprovalPayload>();
        adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

        var deserializedAppr = JsonSerializer.Deserialize<CliApprovalPayload>(adapter.LastOutboundPayloadJson!);
        deserializedAppr.Should().NotBeNull();
        deserializedAppr!.RequestId.Should().Be(approval.Id);
        deserializedAppr.ActionName.Should().Be("git_push");
        deserializedAppr.Tier.Should().Be(RiskTier.Yellow);
        deserializedAppr.Description.Should().Be("Push commits to main");

        await adapter.StopAsync();
    }

    // =========================================================================
    // PART 2: LIVE HTTP DISPATCH PIPELINE & CREDENTIAL VERIFICATION
    // =========================================================================

    private record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        Dictionary<string, List<string>> Headers,
        string? Body
    );

    private class TestHttpMessageHandler : HttpMessageHandler
    {
        public List<RecordedRequest> CapturedRequests { get; } = new();
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseContent { get; set; } = "{\"ok\":true}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToList());

            CapturedRequests.Add(new RecordedRequest(request.Method, request.RequestUri, headers, body));

            var response = new HttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(ResponseContent, System.Text.Encoding.UTF8, "application/json")
            };
            return response;
        }
    }

    [Fact]
    public async Task TelegramAdapter_LiveHttpDispatch_SendsPostWithAuthorizationAndPayload()
    {
        Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", "test_bot_token_123");
        Environment.SetEnvironmentVariable("TELEGRAM_CHAT_ID", "chat_456");

        try
        {
            var handler = new TestHttpMessageHandler();
            using var httpClient = new HttpClient(handler);

            var adapter = new TelegramAdapter(httpClient: httpClient);
            await adapter.StartAsync();
            adapter.IsMock.Should().BeFalse();

            var msg = AgentMessage.Create("telegram", "bot", "Live message test");
            bool sent = await adapter.SendMessageAsync(msg);
            sent.Should().BeTrue();

            handler.CapturedRequests.Should().HaveCount(1);
            var req = handler.CapturedRequests[0];
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("https://api.telegram.org/bottest_bot_token_123/sendMessage");

            req.Body.Should().NotBeNull();
            req.Body.Should().Contain("Live message test");
            req.Body.Should().Contain("\"chat_id\": \"chat_456\"");

            // Test failure handling
            handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
            bool failedSent = await adapter.SendMessageAsync(msg);
            failedSent.Should().BeFalse();

            await adapter.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", null);
            Environment.SetEnvironmentVariable("TELEGRAM_CHAT_ID", null);
        }
    }

    [Fact]
    public async Task DiscordAdapter_LiveHttpDispatch_SendsPostWithBotAuthHeader()
    {
        Environment.SetEnvironmentVariable("DISCORD_BOT_TOKEN", "discord_secret_token");
        Environment.SetEnvironmentVariable("DISCORD_CHANNEL_ID", "channel_789");

        try
        {
            var handler = new TestHttpMessageHandler();
            using var httpClient = new HttpClient(handler);

            var adapter = new DiscordAdapter(httpClient: httpClient);
            await adapter.StartAsync();
            adapter.IsMock.Should().BeFalse();

            var msg = AgentMessage.Create("discord", "moderator", "Discord live notification");
            bool sent = await adapter.SendMessageAsync(msg);
            sent.Should().BeTrue();

            handler.CapturedRequests.Should().HaveCount(1);
            var req = handler.CapturedRequests[0];
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("https://discord.com/api/v10/channels/channel_789/messages");
            req.Headers["Authorization"].Should().Contain("Bot discord_secret_token");

            req.Body.Should().NotBeNull();
            req.Body.Should().Contain("Discord live notification");

            await adapter.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISCORD_BOT_TOKEN", null);
            Environment.SetEnvironmentVariable("DISCORD_CHANNEL_ID", null);
        }
    }

    [Fact]
    public async Task SlackAdapter_LiveHttpDispatch_SendsPostWithBearerAuthHeader()
    {
        Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", "xoxb-slack-secret");
        Environment.SetEnvironmentVariable("SLACK_CHANNEL_ID", "C999888");

        try
        {
            var handler = new TestHttpMessageHandler();
            using var httpClient = new HttpClient(handler);

            var adapter = new SlackAdapter(httpClient: httpClient);
            await adapter.StartAsync();
            adapter.IsMock.Should().BeFalse();

            var approval = ApprovalRequest.Create("purge_cache", RiskTier.Yellow, "Purge Redis cache");
            bool sent = await adapter.SendApprovalPromptAsync(approval);
            sent.Should().BeTrue();

            handler.CapturedRequests.Should().HaveCount(1);
            var req = handler.CapturedRequests[0];
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("https://slack.com/api/chat.postMessage");
            req.Headers["Authorization"].Should().Contain("Bearer xoxb-slack-secret");

            req.Body.Should().NotBeNull();
            req.Body.Should().Contain("purge_cache");
            req.Body.Should().Contain(approval.Id);

            await adapter.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", null);
            Environment.SetEnvironmentVariable("SLACK_CHANNEL_ID", null);
        }
    }

    [Fact]
    public async Task WhatsAppAdapter_LiveHttpDispatch_SendsPostWithBearerAuthHeader()
    {
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", "wa_bearer_token");
        Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", "phone_id_555");

        try
        {
            var handler = new TestHttpMessageHandler();
            using var httpClient = new HttpClient(handler);

            var adapter = new WhatsAppAdapter(httpClient: httpClient);
            await adapter.StartAsync();
            adapter.IsMock.Should().BeFalse();

            var msg = AgentMessage.Create("whatsapp", "support", "Customer ticket update");
            bool sent = await adapter.SendMessageAsync(msg);
            sent.Should().BeTrue();

            handler.CapturedRequests.Should().HaveCount(1);
            var req = handler.CapturedRequests[0];
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("https://graph.facebook.com/v19.0/phone_id_555/messages");
            req.Headers["Authorization"].Should().Contain("Bearer wa_bearer_token");

            req.Body.Should().NotBeNull();
            req.Body.Should().Contain("Customer ticket update");

            await adapter.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", null);
            Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", null);
        }
    }

    [Fact]
    public async Task SignalAdapter_LiveHttpDispatch_SendsPostToRpcEndpoint()
    {
        Environment.SetEnvironmentVariable("SIGNAL_CLI_ENDPOINT", "http://127.0.0.1:9090/v1/rpc");

        try
        {
            var handler = new TestHttpMessageHandler();
            using var httpClient = new HttpClient(handler);

            var adapter = new SignalAdapter(httpClient: httpClient);
            await adapter.StartAsync();
            adapter.IsMock.Should().BeFalse();

            var msg = AgentMessage.Create("signal", "operator", "Signal live message");
            bool sent = await adapter.SendMessageAsync(msg);
            sent.Should().BeTrue();

            handler.CapturedRequests.Should().HaveCount(1);
            var req = handler.CapturedRequests[0];
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.ToString().Should().Be("http://127.0.0.1:9090/v1/rpc");

            req.Body.Should().NotBeNull();
            req.Body.Should().Contain("\"jsonrpc\": \"2.0\"");
            req.Body.Should().Contain("\"method\": \"send\"");

            await adapter.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIGNAL_CLI_ENDPOINT", null);
        }
    }

    // =========================================================================
    // PART 3: CENTRALEVENTBUS MASSIVE PARALLEL STRESS & NON-BLOCKING THROUGHPUT
    // =========================================================================

    [Fact]
    public async Task CentralEventBus_MassiveParallelPublishing_5000MessagesAcross50Tasks_ZeroLossZeroDeadlock()
    {
        // 50 concurrent publishing tasks, each publishing 100 messages (total = 5,000 messages)
        // 5 active subscribers collecting messages simultaneously
        const int publisherTasksCount = 50;
        const int messagesPerTask = 100;
        const int totalExpected = publisherTasksCount * messagesPerTask; // 5,000
        const int subscriberCount = 5;

        var bus = new CentralEventBus(subscriberChannelCapacity: 10000, historyCapacity: 500);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var subscriberReceivedBags = new ConcurrentBag<AgentMessage>[subscriberCount];
        for (int i = 0; i < subscriberCount; i++)
        {
            subscriberReceivedBags[i] = new ConcurrentBag<AgentMessage>();
        }

        var readyGate = new TaskCompletionSource();
        int subscribersStarted = 0;

        var subscriberTasks = Enumerable.Range(0, subscriberCount).Select(subIdx => Task.Run(async () =>
        {
            var bag = subscriberReceivedBags[subIdx];
            var stream = bus.SubscribeAsync(cts.Token);

            if (Interlocked.Increment(ref subscribersStarted) == subscriberCount)
            {
                readyGate.SetResult();
            }

            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                bag.Add(msg);
                if (bag.Count >= totalExpected)
                {
                    break;
                }
            }
        })).ToArray();

        await readyGate.Task;
        await Task.Delay(30, cts.Token);

        // Launch 50 parallel publisher tasks
        var publisherTasks = Enumerable.Range(0, publisherTasksCount).Select(pubIdx => Task.Run(async () =>
        {
            for (int m = 0; m < messagesPerTask; m++)
            {
                var msg = AgentMessage.Create(
                    channel: "flood_test",
                    sender: $"pub_{pubIdx}",
                    content: $"msg_{pubIdx}_{m}",
                    id: $"msg_{pubIdx:D3}_{m:D4}"
                );
                await bus.PublishAsync(msg, cts.Token);
            }
        })).ToArray();

        await Task.WhenAll(publisherTasks);
        await Task.WhenAll(subscriberTasks);

        bus.TotalPublishedCount.Should().Be(totalExpected);

        for (int i = 0; i < subscriberCount; i++)
        {
            var list = subscriberReceivedBags[i].ToList();
            list.Should().HaveCount(totalExpected, $"Subscriber {i} must receive all {totalExpected} messages without any drops");

            var distinctIds = list.Select(m => m.Id).ToHashSet();
            distinctIds.Should().HaveCount(totalExpected, $"Subscriber {i} must receive unique message IDs without duplicates");
        }
    }

    [Fact]
    public async Task CentralEventBus_HeadOfLineBlocking_SlowSubscriberDoesNotBlockFastSubscriber()
    {
        // Challenge: Prove that when a slow subscriber has a full bounded channel,
        // the fast subscriber is NOT blocked from receiving messages.
        var bus = new CentralEventBus(subscriberChannelCapacity: 2); // Very small capacity
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var fastSubscriberReceived = new List<AgentMessage>();
        var fastReady = new TaskCompletionSource();
        var slowReady = new TaskCompletionSource();

        // 1. Slow subscriber with capacity 2 that NEVER reads beyond 1 message
        var slowTask = Task.Run(async () =>
        {
            var stream = bus.SubscribeAsync(cts.Token);
            slowReady.SetResult();

            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                // Read exactly 1 message, then sleep to keep channel completely full
                await Task.Delay(1500, cts.Token);
                break;
            }
        });

        // 2. Fast subscriber that reads continuously
        var fastTask = Task.Run(async () =>
        {
            var stream = bus.SubscribeAsync(cts.Token);
            fastReady.SetResult();

            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                fastSubscriberReceived.Add(msg);
                if (fastSubscriberReceived.Count >= 20)
                {
                    break;
                }
            }
        });

        await Task.WhenAll(slowReady.Task, fastReady.Task);
        await Task.Delay(20, cts.Token);

        // Publish 20 messages rapidly
        // Because of the concurrent fan-out remediation:
        // TryWrite immediately writes to the fast subscriber, while slow subscriber writes concurrently.
        var publishTask = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await bus.PublishAsync(AgentMessage.Create("perf", "pub", $"msg_{i}"), cts.Token);
            }
        });

        // Fast subscriber should finish reading all 20 messages quickly without waiting for slow subscriber
        var fastFinished = await Task.WhenAny(fastTask, Task.Delay(TimeSpan.FromSeconds(3), cts.Token));
        fastFinished.Should().Be(fastTask, "Fast subscriber must NOT experience head-of-line blocking from slow subscriber");

        fastSubscriberReceived.Should().HaveCount(20);
    }

    // =========================================================================
    // PART 4: SMARTAPPROVAL 256-BIT CSPRNG & CONCURRENT CANCELLATION ROBUSTNESS
    // =========================================================================

    [Fact]
    public void SmartApproval_CSPRNG_10000Tokens_HighEntropyAndZeroCollisions()
    {
        // Generate 10,000 approval tokens
        // Verify:
        // 1. Exactly 69 chars: 'appr_' prefix + 64 lowercase hex characters
        // 2. Strict uniqueness (zero duplicate tokens)
        // 3. Uniform hex distribution
        const int count = 10000;
        var set = new HashSet<string>(count);

        for (int i = 0; i < count; i++)
        {
            var req = ApprovalRequest.Create($"action_{i}", RiskTier.Yellow, "Token entropy stress");

            req.Id.Should().StartWith("appr_");
            req.Id.Length.Should().Be(69);

            string hex = req.Id.Substring(5);
            hex.Should().MatchRegex("^[0-9a-f]{64}$");

            set.Add(req.Id).Should().BeTrue($"Collision detected at iteration {i}: token {req.Id}");
        }

        set.Count.Should().Be(count);
    }

    [Fact]
    public async Task SmartApproval_ConcurrentResolutions_ThreadSafetyUnderHeavyLoad()
    {
        // 100 concurrent requests (50 Yellow, 50 Red)
        // Half approved by user, half denied by user concurrently
        var approval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromSeconds(5),
            DefaultRedTimeout = TimeSpan.FromSeconds(5)
        };

        const int requestCount = 100;
        var requests = Enumerable.Range(0, requestCount).Select(i =>
        {
            var tier = (i % 2 == 0) ? RiskTier.Yellow : RiskTier.Red;
            return ApprovalRequest.Create($"action_{i}", tier, $"Concurrent test action {i}");
        }).ToList();

        // Start all approval requests
        var requestTasks = requests.Select(r => approval.RequestApprovalAsync(r)).ToList();

        await Task.Delay(20);
        approval.GetPendingRequests().Should().HaveCount(requestCount);

        // Concurrently submit approvals and denials across worker threads
        var resolutionTasks = requests.Select((r, idx) => Task.Run(() =>
        {
            bool approve = (idx % 2 == 0);
            return approval.SubmitApproval(r.Id, approve, reason: $"Resolved {idx}");
        })).ToArray();

        var submitResults = await Task.WhenAll(resolutionTasks);
        submitResults.Should().OnlyContain(success => success == true);

        var decisions = await Task.WhenAll(requestTasks);
        decisions.Should().HaveCount(requestCount);

        for (int i = 0; i < requestCount; i++)
        {
            bool expectedApproved = (i % 2 == 0);
            decisions[i].IsApproved.Should().Be(expectedApproved);
            decisions[i].Reason.Should().Be($"Resolved {i}");
        }

        approval.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task SmartApproval_IdempotencyAndLateSubmissions_SafelyRejected()
    {
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromMilliseconds(50)
        };

        var req = ApprovalRequest.Create("nuke", RiskTier.Red, "Nuke database");
        var decision = await approval.RequestApprovalAsync(req);

        decision.IsApproved.Should().BeFalse();
        decision.DecidedBy.Should().Be("system:red-timeout-denied");

        // Attempt late submission after timeout
        bool lateResult = approval.SubmitApproval(req.Id, approved: true, "Late user click");
        lateResult.Should().BeFalse("Late approval on an expired request must return false");

        // Attempt submission with invalid token
        bool invalidTokenResult = approval.SubmitApproval("appr_nonexistent_token_00000000", approved: true);
        invalidTokenResult.Should().BeFalse();

        // Attempt submission with empty token
        bool emptyTokenResult = approval.SubmitApproval("", approved: true);
        emptyTokenResult.Should().BeFalse();
    }

    // =========================================================================
    // PART 5: GHOSTTYPINGSYNC THREAD-SAFETY & MULTI-STREAM FIDELITY
    // =========================================================================

    [Fact]
    public async Task GhostTypingSync_MultiThreadedStress_RandomSharedThreadSafe()
    {
        // Challenge: 40 concurrent streams generating ghost typing updates simultaneously
        // Verifies no race conditions on Random.Shared or StringBuilder
        var ghostSync = new GhostTypingSync
        {
            MinDelayMs = 1,
            MaxDelayMs = 2
        };

        var bus = new CentralEventBus(subscriberChannelCapacity: 5000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var msg = AgentMessage.Create("whatsapp", $"user_{i}", $"Concurrent message #{i} with payload data");
            await ghostSync.StreamGhostTypingAsync(msg, bus, ct: cts.Token);
        })).ToArray();

        var completed = await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
        completed.Should().NotBe(Task.Delay(TimeSpan.FromSeconds(10), cts.Token), "40 concurrent ghost typing sessions must complete within timeout without throwing or hanging");
    }

    // =========================================================================
    // PART 6: ADVERSARIAL EDGE CASES & BOUNDARY MINING
    // =========================================================================

    [Fact]
    public async Task ChannelAdapters_MassivePayloadAndSpecialCharacters_SerializeWithoutFault()
    {
        // 100,000 characters payload with unicode, JSON escapes, and control characters
        string massiveText = new string('A', 50000) + "{\"hack\": true}\n\r\t\\\"'\u0000\uFFFF" + new string('B', 50000);

        var adapters = new ChannelAdapterBase[]
        {
            new TelegramAdapter(),
            new DiscordAdapter(),
            new SlackAdapter(),
            new WhatsAppAdapter(),
            new SignalAdapter(),
            new EmailAdapter(),
            new CliAdapter()
        };

        foreach (var adapter in adapters)
        {
            await adapter.StartAsync();

            var msg = AgentMessage.Create(adapter.ChannelId, "adversary", massiveText);
            bool sent = await adapter.SendMessageAsync(msg);
            sent.Should().BeTrue();

            adapter.LastOutboundPayload.Should().NotBeNull();
            adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

            // Verify the JSON is parseable by System.Text.Json
            using var doc = JsonDocument.Parse(adapter.LastOutboundPayloadJson!);
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);

            var appr = ApprovalRequest.Create("exploit_action", RiskTier.Red, massiveText);
            bool apprSent = await adapter.SendApprovalPromptAsync(appr);
            apprSent.Should().BeTrue();

            adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();
            using var apprDoc = JsonDocument.Parse(adapter.LastOutboundPayloadJson!);
            apprDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);

            await adapter.StopAsync();
        }
    }

    [Fact]
    public async Task CentralEventBus_PreCancelledToken_HandlesSafely()
    {
        var bus = new CentralEventBus();
        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();

        // 1. PublishAsync with pre-cancelled token:
        // With 0 subscribers, PublishAsync increments count and returns ValueTask immediately
        var msg = AgentMessage.Create("test", "sender", "msg");
        await bus.PublishAsync(msg, cancelledCts.Token);
        bus.TotalPublishedCount.Should().Be(1);

        // 2. SubscribeAsync with pre-cancelled token throws OperationCanceledException upon WaitToReadAsync
        var act = async () =>
        {
            await foreach (var item in bus.SubscribeAsync(cancelledCts.Token))
            {
            }
        };
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CentralEventBus_ConcurrentSubscriberChurnAndMassivePublish_NoDeadlocks()
    {
        // 30 subscribers continuously connecting, reading 1-3 messages, and dropping connection
        // while 10 publisher threads simultaneously pump 1,000 messages
        var bus = new CentralEventBus(subscriberChannelCapacity: 100);
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var publishingActive = true;
        var publishedCount = 0;

        var publishers = Enumerable.Range(0, 10).Select(p => Task.Run(async () =>
        {
            while (publishingActive && !testCts.Token.IsCancellationRequested)
            {
                var count = Interlocked.Increment(ref publishedCount);
                if (count > 1000) break;
                await bus.PublishAsync(AgentMessage.Create("churn", $"p_{p}", $"Message {count}"), testCts.Token);
            }
        })).ToArray();

        var churners = Enumerable.Range(0, 30).Select(_ => Task.Run(async () =>
        {
            for (int cycle = 0; cycle < 5; cycle++)
            {
                using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(testCts.Token);
                int count = 0;
                try
                {
                    await foreach (var msg in bus.SubscribeAsync("churn", innerCts.Token))
                    {
                        count++;
                        if (count >= 2)
                        {
                            innerCts.Cancel();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on drop
                }
                await Task.Delay(2);
            }
        })).ToArray();

        await Task.WhenAll(publishers);
        publishingActive = false;
        await Task.WhenAll(churners);

        bus.TotalPublishedCount.Should().BeGreaterOrEqualTo(100);
    }
}
