using FluentAssertions;
using HUMofA.Gateway;
using HUMofA.Gateway.Adapters;
using Xunit;

namespace HUMofA.Tests;

public class GatewayTests
{
    [Fact]
    public async Task CentralEventBus_PublishAndSubscribe_DeliversMessage()
    {
        var bus = new CentralEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        AgentMessage? received = null;
        var subTask = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync(cts.Token))
            {
                received = msg;
                break;
            }
        });

        // Give subscription a moment to attach
        await Task.Delay(20);

        var sentMsg = AgentMessage.Create("telegram", "@alex", "Hello HUMofA!");
        await bus.PublishAsync(sentMsg);

        await subTask;

        received.Should().NotBeNull();
        received!.Id.Should().Be(sentMsg.Id);
        received.Channel.Should().Be("telegram");
        received.Sender.Should().Be("@alex");
        received.Content.Should().Be("Hello HUMofA!");
        bus.TotalPublishedCount.Should().Be(1);
    }

    [Fact]
    public async Task CentralEventBus_MultiSubscriber_FansOutToAllSubscribers()
    {
        var bus = new CentralEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var sub1Received = new List<AgentMessage>();
        var sub2Received = new List<AgentMessage>();
        var sub3Received = new List<AgentMessage>();

        var task1 = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync(cts.Token))
            {
                sub1Received.Add(msg);
                if (sub1Received.Count == 2) break;
            }
        });

        var task2 = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync(cts.Token))
            {
                sub2Received.Add(msg);
                if (sub2Received.Count == 2) break;
            }
        });

        var task3 = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync(cts.Token))
            {
                sub3Received.Add(msg);
                if (sub3Received.Count == 2) break;
            }
        });

        await Task.Delay(30);

        await bus.PublishAsync(AgentMessage.Create("discord", "user1", "Message 1"));
        await bus.PublishAsync(AgentMessage.Create("discord", "user2", "Message 2"));

        await Task.WhenAll(task1, task2, task3);

        sub1Received.Should().HaveCount(2);
        sub2Received.Should().HaveCount(2);
        sub3Received.Should().HaveCount(2);
        bus.TotalPublishedCount.Should().Be(2);
    }

    [Fact]
    public async Task CentralEventBus_FilterByChannel_ReceivesOnlyMatchingMessages()
    {
        var bus = new CentralEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var telegramMessages = new List<AgentMessage>();
        var subTask = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync("telegram", cts.Token))
            {
                telegramMessages.Add(msg);
                if (telegramMessages.Count == 1) break;
            }
        });

        await Task.Delay(20);

        // Publish to other channels
        await bus.PublishAsync(AgentMessage.Create("discord", "bot", "Discord payload"));
        await bus.PublishAsync(AgentMessage.Create("cli", "admin", "CLI command"));
        // Now publish to telegram
        await bus.PublishAsync(AgentMessage.Create("telegram", "@alex", "Telegram payload"));

        await subTask;

        telegramMessages.Should().HaveCount(1);
        telegramMessages[0].Channel.Should().Be("telegram");
        telegramMessages[0].Content.Should().Be("Telegram payload");
    }

    [Fact]
    public async Task CentralEventBus_ConcurrentPublishing_IsThreadSafeAndMaintainsCount()
    {
        var bus = new CentralEventBus(subscriberChannelCapacity: 5000, historyCapacity: 500);
        int taskCount = 10;
        int messagesPerTask = 50;

        var tasks = Enumerable.Range(0, taskCount).Select(t => Task.Run(async () =>
        {
            for (int i = 0; i < messagesPerTask; i++)
            {
                await bus.PublishAsync(AgentMessage.Create("loadtest", $"thread_{t}", $"Item {i}"));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        bus.TotalPublishedCount.Should().Be(taskCount * messagesPerTask);
        var history = bus.GetRecentHistory(100);
        history.Should().HaveCount(100);
    }

    [Fact]
    public async Task GhostTypingSync_StreamsCharacters_SequentiallyAndEmitsFinalReasoningTrigger()
    {
        var bus = new CentralEventBus();
        var ghostSync = new GhostTypingSync
        {
            MinDelayMs = 1,
            MaxDelayMs = 2
        };

        var streamedMessages = new List<AgentMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subTask = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync("desktop", cts.Token))
            {
                streamedMessages.Add(msg);
                if (msg.Metadata != null && msg.Metadata.TryGetValue("isFinal", out string? isFinal) && isFinal == "true")
                {
                    break;
                }
            }
        });

        await Task.Delay(20);

        var inbound = AgentMessage.Create("telegram", "@user", "Hi AI");
        await ghostSync.StreamGhostTypingAsync(inbound, bus, pacingDelay: TimeSpan.FromMilliseconds(2), ct: cts.Token);

        await subTask;

        // "Hi AI" is 5 chars -> 5 ghost typing messages + 1 final completed message = 6 messages
        streamedMessages.Should().HaveCount(6);

        // First 5 must be ghost typing
        for (int i = 0; i < 5; i++)
        {
            streamedMessages[i].IsGhostTyping.Should().BeTrue();
            streamedMessages[i].Channel.Should().Be("desktop");
            streamedMessages[i].Metadata!["charIndex"].Should().Be(i.ToString());
        }

        // Contents progressive check: H, Hi, Hi , Hi A, Hi AI
        streamedMessages[0].Content.Should().Be("H");
        streamedMessages[1].Content.Should().Be("Hi");
        streamedMessages[2].Content.Should().Be("Hi ");
        streamedMessages[3].Content.Should().Be("Hi A");
        streamedMessages[4].Content.Should().Be("Hi AI");

        // Last message is the finalized trigger
        var finalMsg = streamedMessages[5];
        finalMsg.IsGhostTyping.Should().BeFalse();
        finalMsg.Content.Should().Be("Hi AI");
        finalMsg.Metadata!["isFinal"].Should().Be("true");
        finalMsg.Metadata["triggerAgentReasoning"].Should().Be("true");
    }

    [Fact]
    public async Task GhostTypingSync_StreamUpdatesAsync_YieldsProgressiveUpdates()
    {
        var ghostSync = new GhostTypingSync();
        var updates = new List<GhostTypingUpdate>();

        await foreach (var update in ghostSync.StreamUpdatesAsync("Test", pacingDelay: TimeSpan.Zero))
        {
            updates.Add(update);
        }

        updates.Should().HaveCount(4);
        updates[0].PartialContent.Should().Be("T");
        updates[0].IsCompleted.Should().BeFalse();
        updates[3].PartialContent.Should().Be("Test");
        updates[3].IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task GhostTypingSync_EmptyMessage_PublishesFinalMessageImmediately()
    {
        var bus = new CentralEventBus();
        var ghostSync = new GhostTypingSync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        AgentMessage? finalMsg = null;
        var subTask = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync("desktop", cts.Token))
            {
                finalMsg = msg;
                break;
            }
        });

        await Task.Delay(20);

        var emptyInbound = AgentMessage.Create("telegram", "@user", "");
        await ghostSync.StreamGhostTypingAsync(emptyInbound, bus, pacingDelay: TimeSpan.Zero, ct: cts.Token);

        await subTask;

        finalMsg.Should().NotBeNull();
        finalMsg!.Content.Should().BeEmpty();
        finalMsg.Metadata!["isFinal"].Should().Be("true");
    }

    [Theory]
    [InlineData("som", RiskTier.Green)]
    [InlineData("list_windows", RiskTier.Green)]
    [InlineData("read_file", RiskTier.Green)]
    [InlineData("web_search", RiskTier.Green)]
    [InlineData("write_file", RiskTier.Yellow)]
    [InlineData("git_commit", RiskTier.Yellow)]
    [InlineData("open_browser", RiskTier.Yellow)]
    [InlineData("format_drive", RiskTier.Red)]
    [InlineData("delete_database", RiskTier.Red)]
    [InlineData("admin_exec", RiskTier.Red)]
    public void SmartApproval_ClassifyAction_ReturnsExpectedRiskTier(string actionName, RiskTier expectedTier)
    {
        var smartApproval = new SmartApproval();
        var tier = smartApproval.ClassifyAction(actionName);
        tier.Should().Be(expectedTier);
    }

    [Fact]
    public void SmartApproval_ClassifyAction_ParametersWithForbiddenKeywords_ElevatesToRed()
    {
        var smartApproval = new SmartApproval();
        var parameters = new Dictionary<string, string>
        {
            ["command"] = "rm -rf /var/data",
            ["secret"] = "my_api_key"
        };

        var tier = smartApproval.ClassifyAction("execute_terminal", parameters);
        tier.Should().Be(RiskTier.Red);
    }

    [Fact]
    public async Task SmartApproval_GreenTier_AutoApprovesImmediately()
    {
        var smartApproval = new SmartApproval();
        var request = ApprovalRequest.Create("read_file", RiskTier.Green, "Read README.md");

        var decision = await smartApproval.RequestApprovalAsync(request);

        decision.IsApproved.Should().BeTrue();
        decision.Tier.Should().Be(RiskTier.Green);
        decision.DecidedBy.Should().Contain("green");
    }

    [Fact]
    public async Task SmartApproval_YellowTier_AutoApprovesAfterGracePeriod_IfNotCancelled()
    {
        var smartApproval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromMilliseconds(50)
        };

        var request = ApprovalRequest.Create("edit_code", RiskTier.Yellow, "Refactor service");
        var decision = await smartApproval.RequestApprovalAsync(request);

        decision.IsApproved.Should().BeTrue();
        decision.Tier.Should().Be(RiskTier.Yellow);
        decision.DecidedBy.Should().Contain("grace-elapsed");
    }

    [Fact]
    public async Task SmartApproval_YellowTier_CancelsDuringGracePeriod()
    {
        var smartApproval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromSeconds(2)
        };

        var request = ApprovalRequest.Create("edit_code", RiskTier.Yellow, "Refactor service");

        var requestTask = smartApproval.RequestApprovalAsync(request);

        // Cancel during grace period
        await Task.Delay(30);
        bool cancelled = smartApproval.CancelYellowGracePeriod(request.Id, "User clicked cancel");
        cancelled.Should().BeTrue();

        var decision = await requestTask;

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Contain("User clicked cancel");
    }

    [Fact]
    public async Task SmartApproval_RedTier_ExplicitApproval_Succeeds()
    {
        var smartApproval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromSeconds(5)
        };

        var request = ApprovalRequest.Create(
            "delete_database",
            RiskTier.Red,
            "Drop database customers",
            screenshotBase64: "base64ScreenshotData"
        );

        var requestTask = smartApproval.RequestApprovalAsync(request);

        await Task.Delay(30);
        smartApproval.GetPendingRequests().Should().ContainSingle(r => r.Id == request.Id);

        bool approved = smartApproval.SubmitApproval(request.Id, approved: true, reason: "Confirmed by Senior SRE", approvedBy: "user:@security_officer");
        approved.Should().BeTrue();

        var decision = await requestTask;
        decision.IsApproved.Should().BeTrue();
        decision.Reason.Should().Be("Confirmed by Senior SRE");
        decision.DecidedBy.Should().Be("user:@security_officer");
    }

    [Fact]
    public async Task SmartApproval_RedTier_ExplicitDenial_ReturnsDenied()
    {
        var smartApproval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromSeconds(5)
        };

        var request = ApprovalRequest.Create("wipe_disk", RiskTier.Red, "Wipe test disk");

        var requestTask = smartApproval.RequestApprovalAsync(request);

        await Task.Delay(30);
        bool denied = smartApproval.SubmitApproval(request.Id, approved: false, reason: "Unauthorized attempt", approvedBy: "user:@admin");
        denied.Should().BeTrue();

        var decision = await requestTask;
        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be("Unauthorized attempt");
    }

    [Fact]
    public async Task SmartApproval_RedTier_Timeout_DefaultsToDeny()
    {
        var smartApproval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromMilliseconds(50)
        };

        var request = ApprovalRequest.Create("format_volume", RiskTier.Red, "Format volume D:");
        var decision = await smartApproval.RequestApprovalAsync(request);

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Contain("timed out");
    }

    [Fact]
    public async Task ChannelAdapters_WhenEnvMissing_DefaultGracefullyToMockMode()
    {
        // Clear env vars to simulate zero-credential environment
        Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", null);
        Environment.SetEnvironmentVariable("DISCORD_BOT_TOKEN", null);
        Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", null);
        Environment.SetEnvironmentVariable("WHATSAPP_API_KEY", null);
        Environment.SetEnvironmentVariable("SIGNAL_API_URL", null);
        Environment.SetEnvironmentVariable("EMAIL_SMTP_HOST", null);
        Environment.SetEnvironmentVariable("CLI_ENABLED", "false");

        var adapters = new IChannelAdapter[]
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

            adapter.IsConnected.Should().BeTrue($"Adapter {adapter.ChannelId} should connect in mock mode");
            adapter.IsMock.Should().BeTrue($"Adapter {adapter.ChannelId} should gracefully default to IsMock=true");

            // Verify safe send message in mock mode
            bool sent = await adapter.SendMessageAsync(AgentMessage.Create(adapter.ChannelId, "sender", "Test payload"));
            sent.Should().BeTrue();
            adapter.SentMessages.Should().HaveCount(1);

            // Verify safe send approval prompt in mock mode
            var req = ApprovalRequest.Create("test_action", RiskTier.Red, "Test approval prompt");
            bool prompted = await adapter.SendApprovalPromptAsync(req);
            prompted.Should().BeTrue();
            adapter.SentApprovals.Should().HaveCount(1);

            await adapter.StopAsync();
            adapter.IsConnected.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ChannelAdapters_SimulateInboundMessage_PublishesToCentralEventBus()
    {
        var bus = new CentralEventBus();
        var adapter = new TelegramAdapter(bus);
        await adapter.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        AgentMessage? received = null;

        var subTask = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync("telegram", cts.Token))
            {
                received = msg;
                break;
            }
        });

        await Task.Delay(20);

        var inbound = AgentMessage.Create("telegram", "@alex_remote", "Remote command: /status");
        adapter.SimulateInboundMessage(inbound);

        await subTask;

        received.Should().NotBeNull();
        received!.Sender.Should().Be("@alex_remote");
        received.Content.Should().Be("Remote command: /status");
    }

    [Fact]
    public async Task MockChannelAdapter_AutoApproveOnPrompt_WorksWithSmartApproval()
    {
        var smartApproval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromSeconds(3)
        };

        var mockAdapter = new MockChannelAdapter
        {
            AutoApproveOnPrompt = true,
            AttachedSmartApproval = smartApproval
        };
        await mockAdapter.StartAsync();

        var request = ApprovalRequest.Create("critical_action", RiskTier.Red, "Needs auto test approval");

        var requestTask = smartApproval.RequestApprovalAsync(request);

        // Adapter receives the prompt and triggers auto approval
        await mockAdapter.SendApprovalPromptAsync(request);

        var decision = await requestTask;
        decision.IsApproved.Should().BeTrue();
        decision.DecidedBy.Should().Be("mock:auto");
    }
}
