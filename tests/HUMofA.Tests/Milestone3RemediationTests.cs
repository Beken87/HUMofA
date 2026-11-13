using System.Text.Json;
using FluentAssertions;
using HUMofA.Gateway;
using HUMofA.Gateway.Adapters;
using Xunit;

namespace HUMofA.Tests;

public class Milestone3RemediationTests
{
    [Fact]
    public void ChannelPayloads_AllSevenChannels_SerializeAccurately()
    {
        // 1. Telegram
        var tgKeyboard = new TelegramInlineKeyboard(new[]
        {
            new[] { new TelegramInlineKeyboardButton("Approve", CallbackData: "appr:1:yes") }
        });
        var tgPayload = new TelegramMessagePayload("12345", "Test message", "MarkdownV2", tgKeyboard);
        string tgJson = JsonSerializer.Serialize(tgPayload);
        tgJson.Should().Contain("\"chat_id\":\"12345\"");
        tgJson.Should().Contain("\"inline_keyboard\"");

        // 2. Discord
        var dcRow = new DiscordActionRow(1, new[] { new DiscordButton(2, 3, "Approve", "appr_1") });
        var dcPayload = new DiscordMessagePayload("content", new[] { new DiscordEmbed("Title", "Desc", 0x2ECC71) }, new[] { dcRow });
        string dcJson = JsonSerializer.Serialize(dcPayload);
        dcJson.Should().Contain("\"embeds\"");
        dcJson.Should().Contain("\"components\"");

        // 3. Slack
        var slSection = new SlackSectionBlock("section", new SlackTextObject("mrkdwn", "Hello"));
        var slActions = new SlackActionsBlock("actions", "b1", new[] { new SlackButtonElement("button", new SlackTextObject("plain_text", "Btn"), "act1", "val1", "primary") });
        var slPayload = new SlackMessagePayload("C123", "Fallback", new object[] { slSection, slActions });
        string slJson = JsonSerializer.Serialize(slPayload);
        slJson.Should().Contain("\"channel\":\"C123\"");
        slJson.Should().Contain("\"blocks\"");

        // 4. WhatsApp
        var waButtons = new WhatsAppButtonAction(new[] { new WhatsAppButton("reply", new WhatsAppButtonReply("btn_1", "Yes")) });
        var waInteractive = new WhatsAppInteractiveObject("button", new WhatsAppHeaderObject("text", "Header"), new WhatsAppTextObject("Body"), new WhatsAppTextObject("Footer"), waButtons);
        var waPayload = new WhatsAppInteractivePayload(To: "1234567890", Interactive: waInteractive);
        string waJson = JsonSerializer.Serialize(waPayload);
        waJson.Should().Contain("\"messaging_product\":\"whatsapp\"");
        waJson.Should().Contain("\"interactive\"");

        // 5. Signal
        var sigParams = new SignalSendParams(new[] { "+1234567890" }, "Signal test");
        var sigPayload = new SignalJsonRpcRequest(Params: sigParams, Id: "req_1");
        string sigJson = JsonSerializer.Serialize(sigPayload);
        sigJson.Should().Contain("\"jsonrpc\":\"2.0\"");
        sigJson.Should().Contain("\"method\":\"send\"");

        // 6. Email
        var emPayload = new EmailMessagePayload("from@test.com", new[] { "to@test.com" }, "Subj", "Text", "<p>Html</p>");
        string emJson = JsonSerializer.Serialize(emPayload);
        emJson.Should().Contain("from@test.com");
        emJson.Should().Contain("Subj");

        // 7. CLI
        var cliPayload = new CliApprovalPayload("appr_1", "git_commit", RiskTier.Yellow, "Commit changes");
        string cliJson = JsonSerializer.Serialize(cliPayload);
        cliJson.Should().Contain("appr_1");
        cliJson.Should().Contain("git_commit");
    }

    [Fact]
    public async Task ChannelAdapters_OutboundPayloads_RecordedAndSerializedInBase()
    {
        var bus = new CentralEventBus();
        var adapters = new ChannelAdapterBase[]
        {
            new TelegramAdapter(bus),
            new DiscordAdapter(bus),
            new SlackAdapter(bus),
            new WhatsAppAdapter(bus),
            new SignalAdapter(bus),
            new EmailAdapter(bus),
            new CliAdapter(bus)
        };

        foreach (var adapter in adapters)
        {
            await adapter.StartAsync();

            // 1. Send normal message
            var msg = AgentMessage.Create(adapter.ChannelId, "tester", $"Hello on {adapter.ChannelId}");
            bool sent = await adapter.SendMessageAsync(msg);
            sent.Should().BeTrue();
            adapter.LastOutboundPayload.Should().NotBeNull();
            adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();

            // 2. Send approval prompt
            var appr = ApprovalRequest.Create("format_drive", RiskTier.Red, "Destructive format");
            bool prompted = await adapter.SendApprovalPromptAsync(appr);
            prompted.Should().BeTrue();
            adapter.LastOutboundPayload.Should().NotBeNull();
            adapter.LastOutboundPayloadJson.Should().NotBeNullOrWhiteSpace();
            adapter.LastOutboundPayloadJson.Should().Contain(appr.Id);

            await adapter.StopAsync();
        }
    }

    [Fact]
    public async Task ChannelAdapters_DualEnvironmentVariables_VerifiedProperly()
    {
        // Telegram with TELEGRAM_ALLOWED_CHAT_IDS
        Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", "fake_bot_token");
        Environment.SetEnvironmentVariable("TELEGRAM_CHAT_ID", null);
        Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_CHAT_IDS", "999888");

        var tg = new TelegramAdapter();
        await tg.StartAsync();
        tg.IsMock.Should().BeFalse("Telegram should detect credentials via TELEGRAM_ALLOWED_CHAT_IDS");
        await tg.StopAsync();

        Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", null);
        Environment.SetEnvironmentVariable("TELEGRAM_ALLOWED_CHAT_IDS", null);

        // WhatsApp with WHATSAPP_ACCESS_TOKEN
        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", "fake_access_token");
        Environment.SetEnvironmentVariable("WHATSAPP_API_KEY", null);
        Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", "12345");

        var wa = new WhatsAppAdapter();
        await wa.StartAsync();
        wa.IsMock.Should().BeFalse("WhatsApp should detect credentials via WHATSAPP_ACCESS_TOKEN");
        await wa.StopAsync();

        Environment.SetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN", null);
        Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", null);

        // Signal with SIGNAL_CLI_ENDPOINT
        Environment.SetEnvironmentVariable("SIGNAL_CLI_ENDPOINT", "http://localhost:8080");
        Environment.SetEnvironmentVariable("SIGNAL_API_URL", null);

        var sig = new SignalAdapter();
        await sig.StartAsync();
        sig.IsMock.Should().BeFalse("Signal should detect credentials via SIGNAL_CLI_ENDPOINT");
        await sig.StopAsync();

        Environment.SetEnvironmentVariable("SIGNAL_CLI_ENDPOINT", null);
    }

    [Fact]
    public void SmartApproval_CryptographicRandomness_Generates256BitSecureTokens()
    {
        var tokens = new HashSet<string>();

        for (int i = 0; i < 100; i++)
        {
            var req = ApprovalRequest.Create($"action_{i}", RiskTier.Yellow, "Testing secure tokens");

            // Format check: "appr_" + 64 hex characters (256 bits = 32 bytes)
            req.Id.Should().StartWith("appr_");
            req.Id.Length.Should().Be(69, "Token must be 'appr_' (5 chars) followed by 64 hex characters (32 bytes)");

            string hexPart = req.Id.Substring(5);
            hexPart.Should().MatchRegex("^[0-9a-f]{64}$", "Token hex portion must be lower-case hex characters");

            tokens.Add(req.Id).Should().BeTrue("Each cryptographic token must be strictly unique");
        }
    }

    [Fact]
    public async Task SmartApproval_CancellationAttribution_ReportsSystemCancelled()
    {
        var smartApproval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromSeconds(10),
            DefaultRedTimeout = TimeSpan.FromSeconds(10)
        };

        // Yellow tier token cancellation
        using (var yellowCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
        {
            var yellowReq = ApprovalRequest.Create("edit_code", RiskTier.Yellow, "Modify file");
            var decision = await smartApproval.RequestApprovalAsync(yellowReq, yellowCts.Token);

            decision.IsApproved.Should().BeFalse();
            decision.DecidedBy.Should().Be("system:cancelled");
            decision.Reason.Should().Contain("cancelled");
        }

        // Red tier token cancellation
        using (var redCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
        {
            var redReq = ApprovalRequest.Create("format_disk", RiskTier.Red, "Wipe hard drive");
            var decision = await smartApproval.RequestApprovalAsync(redReq, redCts.Token);

            decision.IsApproved.Should().BeFalse();
            decision.DecidedBy.Should().Be("system:cancelled");
            decision.Reason.Should().Contain("cancelled");
        }
    }

    [Fact]
    public async Task CentralEventBus_HighThroughputFastPath_NonBlockingFanOut()
    {
        var bus = new CentralEventBus(subscriberChannelCapacity: 50);

        var sub1Received = 0;
        var sub2Received = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var t1 = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync("test_chan", cts.Token))
            {
                Interlocked.Increment(ref sub1Received);
                if (sub1Received >= 20) break;
            }
        });

        var t2 = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync("test_chan", cts.Token))
            {
                Interlocked.Increment(ref sub2Received);
                if (sub2Received >= 20) break;
            }
        });

        while (bus.SubscriberCount < 2 && !cts.IsCancellationRequested)
        {
            await Task.Delay(10);
        }

        // Publish 20 messages rapidly through fast path
        for (int i = 0; i < 20; i++)
        {
            await bus.PublishAsync(AgentMessage.Create("test_chan", "perf_tester", $"Msg {i}"));
        }

        await Task.WhenAll(t1, t2);

        sub1Received.Should().Be(20);
        sub2Received.Should().Be(20);
        bus.TotalPublishedCount.Should().BeGreaterOrEqualTo(20);
    }
}
