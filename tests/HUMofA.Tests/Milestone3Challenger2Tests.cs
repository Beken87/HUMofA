using System.Diagnostics;
using FluentAssertions;
using HUMofA.Gateway;
using HUMofA.Gateway.Adapters;
using HUMofA.Scheduler;
using Xunit;
using Xunit.Abstractions;

namespace HUMofA.Tests;

public class Milestone3Challenger2Tests
{
    private readonly ITestOutputHelper _output;

    public Milestone3Challenger2Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    // =========================================================================
    // TASK 2: NaturalCronScheduler Phrasing & Calendar Calculations
    // =========================================================================

    [Theory]
    [InlineData("every 1 minute", "* * * * *")]
    [InlineData("every minute", "* * * * *")]
    [InlineData("every 5 minutes", "*/5 * * * *")]
    [InlineData("every 15 minutes", "*/15 * * * *")]
    [InlineData("every 30 minutes", "*/30 * * * *")]
    [InlineData("every 59 minutes", "*/59 * * * *")]
    [InlineData("every 1 hour", "0 * * * *")]
    [InlineData("every hour", "0 * * * *")]
    [InlineData("every 2 hours", "0 */2 * * *")]
    [InlineData("every 6 hours", "0 */6 * * *")]
    [InlineData("every 12 hours", "0 */12 * * *")]
    [InlineData("every 23 hours", "0 */23 * * *")]
    [InlineData("daily at 00:00", "0 0 * * *")]
    [InlineData("daily at 12am", "0 0 * * *")]
    [InlineData("daily at 12:00", "0 12 * * *")]
    [InlineData("daily at 12pm", "0 12 * * *")]
    [InlineData("daily at 23:59", "59 23 * * *")]
    [InlineData("every day at 11:30pm", "30 23 * * *")]
    [InlineData("every morning at 6am", "0 6 * * *")]
    [InlineData("every evening at 8pm", "0 20 * * *")]
    [InlineData("every day at 3", "0 3 * * *")]
    [InlineData("every monday at 9am", "0 9 * * 1")]
    [InlineData("every tuesday at 14:15", "15 14 * * 2")]
    [InlineData("every wednesday at 3pm", "0 15 * * 3")]
    [InlineData("every thursday at 11:45pm", "45 23 * * 4")]
    [InlineData("every friday at 08:00", "0 8 * * 5")]
    [InlineData("every saturday at 10:30", "30 10 * * 6")]
    [InlineData("every sunday at 12pm", "0 12 * * 0")]
    [InlineData("every sunday midnight", "0 0 * * 0")]
    [InlineData("every monday midnight", "0 0 * * 1")]
    [InlineData("every friday midnight", "0 0 * * 5")]
    [InlineData("weekdays at 08:30", "30 8 * * 1-5")]
    [InlineData("every weekday at 5pm", "0 17 * * 1-5")]
    [InlineData("weekdays at 17:00", "0 17 * * 1-5")]
    public void NaturalCronScheduler_ComplexNaturalPhrasing_ParsesToExpectedCron(string phrase, string expectedCron)
    {
        var parsed = NaturalCronScheduler.Parse(phrase);
        _output.WriteLine($"[Natural Phrasing] '{phrase}' -> '{parsed.CronExpression}' (expected '{expectedCron}')");
        parsed.CronExpression.Should().Be(expectedCron);
        parsed.NextOccurrenceCalculator.Should().NotBeNull();
    }

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 9 * * 1-5")]
    [InlineData("0,15,30,45 * * * *")]
    [InlineData("0 9-17 * * 1-5")]
    [InlineData("30 4 1 * *")]
    [InlineData("0 0 1 1 *")]
    public void NaturalCronScheduler_StandardCronSyntax_ParsesCorrectly(string standardCron)
    {
        var parsed = NaturalCronScheduler.Parse(standardCron);
        parsed.CronExpression.Should().Be(standardCron);
        var refTime = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        var next = parsed.NextOccurrenceCalculator(refTime);
        next.Should().BeAfter(refTime);
    }

    [Theory]
    [InlineData("yearly at Christmas")]
    [InlineData("every century")]
    [InlineData("whenever ready")]
    [InlineData("0 0 0 0")] // 4 parts
    [InlineData("0 0 0 0 0 0")] // 6 parts
    [InlineData("foo bar baz qux quux")]
    [InlineData("")]
    [InlineData("   ")]
    public void NaturalCronScheduler_InvalidPhrasing_ThrowsArgumentException(string invalidPhrase)
    {
        var act = () => NaturalCronScheduler.Parse(invalidPhrase);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NaturalCronScheduler_NullExpression_ThrowsArgumentNullException()
    {
        var act = () => NaturalCronScheduler.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void NaturalCronScheduler_CalendarBoundary_MonthRollover()
    {
        // 2026-01-31 23:55:00 UTC -> every 10 minutes should roll into 2026-02-01 00:00:00 UTC
        var refTime = new DateTime(2026, 1, 31, 23, 55, 0, DateTimeKind.Utc);
        var next = NaturalCronScheduler.GetNextOccurrence("every 10 minutes", refTime);

        _output.WriteLine($"[Month Rollover] Ref: {refTime:u} -> Next: {next:u}");
        next.Should().Be(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void NaturalCronScheduler_CalendarBoundary_LeapYearFebruary29()
    {
        // 2024 is a leap year. Feb 28 23:30 UTC -> daily at 09:00 must schedule Feb 29 09:00 UTC!
        var leapRef = new DateTime(2024, 2, 28, 23, 30, 0, DateTimeKind.Utc);
        var nextLeap = NaturalCronScheduler.GetNextOccurrence("daily at 09:00", leapRef);

        _output.WriteLine($"[Leap Year] Ref: {leapRef:u} -> Next: {nextLeap:u}");
        nextLeap.Year.Should().Be(2024);
        nextLeap.Month.Should().Be(2);
        nextLeap.Day.Should().Be(29);
        nextLeap.Hour.Should().Be(9);
        nextLeap.Minute.Should().Be(0);

        // Next from Feb 29 23:30 UTC -> March 1 09:00 UTC
        var marchRef = new DateTime(2024, 2, 29, 23, 30, 0, DateTimeKind.Utc);
        var nextMarch = NaturalCronScheduler.GetNextOccurrence("daily at 09:00", marchRef);

        _output.WriteLine($"[Post-Leap Day] Ref: {marchRef:u} -> Next: {nextMarch:u}");
        nextMarch.Year.Should().Be(2024);
        nextMarch.Month.Should().Be(3);
        nextMarch.Day.Should().Be(1);
    }

    [Fact]
    public void NaturalCronScheduler_CalendarBoundary_YearRollover()
    {
        // 2026-12-31 23:30:00 UTC -> every hour should roll into 2027-01-01 00:00:00 UTC
        var refTime = new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc);
        var next = NaturalCronScheduler.GetNextOccurrence("every hour", refTime);

        _output.WriteLine($"[Year Rollover] Ref: {refTime:u} -> Next: {next:u}");
        next.Should().Be(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void NaturalCronScheduler_CalendarBoundary_WeekendSkipToMonday()
    {
        // 2026-11-06 18:00:00 UTC is Friday evening.
        // "weekdays at 09:00" must skip Saturday (Nov 7) and Sunday (Nov 8), landing on Monday (Nov 9) at 09:00.
        var fridayEvening = new DateTime(2026, 11, 6, 18, 0, 0, DateTimeKind.Utc);
        fridayEvening.DayOfWeek.Should().Be(DayOfWeek.Friday);

        var next = NaturalCronScheduler.GetNextOccurrence("weekdays at 09:00", fridayEvening);

        _output.WriteLine($"[Weekend Skip] Friday Ref: {fridayEvening:u} -> Next: {next:u} ({next.DayOfWeek})");
        next.DayOfWeek.Should().Be(DayOfWeek.Monday);
        next.Year.Should().Be(2026);
        next.Month.Should().Be(11);
        next.Day.Should().Be(9);
        next.Hour.Should().Be(9);
        next.Minute.Should().Be(0);
    }

    [Fact]
    public void NaturalCronScheduler_CalendarBoundary_DayOfWeekFutureJump()
    {
        // 2026-11-03 10:00:00 UTC is Tuesday.
        // "every monday at 10:00" must jump 6 days to Monday 2026-11-09 10:00:00 UTC.
        var tuesday = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        tuesday.DayOfWeek.Should().Be(DayOfWeek.Tuesday);

        var next = NaturalCronScheduler.GetNextOccurrence("every monday at 10:00", tuesday);

        _output.WriteLine($"[DayOfWeek Jump] Tuesday Ref: {tuesday:u} -> Next Monday: {next:u} ({next.DayOfWeek})");
        next.DayOfWeek.Should().Be(DayOfWeek.Monday);
        next.Day.Should().Be(9);
        next.Hour.Should().Be(10);
    }

    // =========================================================================
    // TASK 2: Background Runner Concurrency, Lifecycle & Error Resilience
    // =========================================================================

    [Fact]
    public async Task NaturalCronScheduler_BackgroundRunner_MultiJobConcurrentExecution()
    {
        var scheduler = new NaturalCronScheduler
        {
            PollingInterval = TimeSpan.FromMilliseconds(20)
        };

        const int jobCount = 5;
        var jobCompletions = new TaskCompletionSource<bool>[jobCount];
        var jobs = new CronJob[jobCount];

        for (int i = 0; i < jobCount; i++)
        {
            int index = i;
            jobCompletions[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            jobs[i] = scheduler.RegisterJob($"ParallelJob_{index}", "every minute", _ =>
            {
                jobCompletions[index].TrySetResult(true);
                return Task.FromResult($"Job {index} finished");
            });

            // Set NextRun in the past so all 5 are immediately due
            jobs[i].NextRun = DateTime.UtcNow.AddSeconds(-10);
        }

        await scheduler.StartAsync();

        var allCompletedTask = Task.WhenAll(jobCompletions.Select(c => c.Task));
        var timeoutTask = Task.Delay(3000);

        var winner = await Task.WhenAny(allCompletedTask, timeoutTask);
        await scheduler.StopAsync();

        winner.Should().Be(allCompletedTask, "All 5 concurrent background jobs should execute within timeout");

        for (int i = 0; i < jobCount; i++)
        {
            jobs[i].RunCount.Should().BeGreaterThanOrEqualTo(1);
            jobs[i].Status.Should().Be(CronJobStatus.Active);
            jobs[i].LastResult.Should().Be($"Job {i} finished");
            jobs[i].NextRun.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task NaturalCronScheduler_BackgroundRunner_PausedAndCancelledJobs_AreSkipped()
    {
        var scheduler = new NaturalCronScheduler
        {
            PollingInterval = TimeSpan.FromMilliseconds(20)
        };

        var activeRun = false;
        var pausedRan = false;
        var cancelledRan = false;

        var activeJob = scheduler.RegisterJob("Active", "every minute", _ =>
        {
            activeRun = true;
            return Task.FromResult("Active OK");
        });
        activeJob.NextRun = DateTime.UtcNow.AddSeconds(-5);

        var pausedJob = scheduler.RegisterJob("Paused", "every minute", _ =>
        {
            pausedRan = true;
            return Task.FromResult("Paused Ran");
        });
        pausedJob.NextRun = DateTime.UtcNow.AddSeconds(-5);
        scheduler.PauseJob(pausedJob.Id);

        var cancelledJob = scheduler.RegisterJob("Cancelled", "every minute", _ =>
        {
            cancelledRan = true;
            return Task.FromResult("Cancelled Ran");
        });
        cancelledJob.NextRun = DateTime.UtcNow.AddSeconds(-5);
        scheduler.CancelJob(cancelledJob.Id);

        await scheduler.StartAsync();
        await Task.Delay(250);
        await scheduler.StopAsync();

        activeRun.Should().BeTrue("Active job must run");
        pausedRan.Should().BeFalse("Paused job must NOT run in background runner");
        cancelledRan.Should().BeFalse("Cancelled job must NOT run in background runner");
    }

    [Fact]
    public async Task NaturalCronScheduler_BackgroundRunner_ExceptionInJob_RecoversAndContinues()
    {
        var bus = new CentralEventBus();
        var scheduler = new NaturalCronScheduler(bus)
        {
            PollingInterval = TimeSpan.FromMilliseconds(20)
        };

        var failRunCount = 0;
        var failingJob = scheduler.RegisterJob("FailingJob", "every minute", _ =>
        {
            failRunCount++;
            throw new ApplicationException("Simulated background job crash");
        });
        failingJob.NextRun = DateTime.UtcNow.AddSeconds(-5);

        await scheduler.StartAsync();
        await Task.Delay(250);
        await scheduler.StopAsync();

        failRunCount.Should().BeGreaterThanOrEqualTo(1);
        failingJob.Status.Should().Be(CronJobStatus.Active, "Status must recover to Active after catching exception");
        failingJob.LastResult.Should().Contain("Simulated background job crash");

        var history = bus.GetRecentHistory(10);
        history.Should().Contain(msg => msg.Channel == "scheduler" && msg.Content.Contains("Simulated background job crash"));
    }

    // =========================================================================
    // TASK 3: All 7 Channel Adapters Graceful Mock Degradation
    // =========================================================================

    [Fact]
    public async Task ChannelAdapters_All7Adapters_GracefulMockDegradation_WhenEnvAbsent()
    {
        // Explicitly clear all credentials to guarantee zero-credential environment
        Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", null);
        Environment.SetEnvironmentVariable("TELEGRAM_CHAT_ID", null);
        Environment.SetEnvironmentVariable("DISCORD_BOT_TOKEN", null);
        Environment.SetEnvironmentVariable("DISCORD_CHANNEL_ID", null);
        Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", null);
        Environment.SetEnvironmentVariable("SLACK_CHANNEL_ID", null);
        Environment.SetEnvironmentVariable("WHATSAPP_API_KEY", null);
        Environment.SetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID", null);
        Environment.SetEnvironmentVariable("SIGNAL_API_URL", null);
        Environment.SetEnvironmentVariable("SIGNAL_PHONE_NUMBER", null);
        Environment.SetEnvironmentVariable("EMAIL_SMTP_HOST", null);
        Environment.SetEnvironmentVariable("EMAIL_USERNAME", null);
        Environment.SetEnvironmentVariable("EMAIL_PASSWORD", null);
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

        adapters.Length.Should().Be(7, "Must test exactly all 7 channel adapters");

        foreach (var adapter in adapters)
        {
            _output.WriteLine($"[Testing Adapter] ID: {adapter.ChannelId}, Type: {adapter.Type}");

            // 1. Start without throwing
            var startTask = () => adapter.StartAsync();
            await startTask.Should().NotThrowAsync();

            // 2. Status verification
            adapter.IsConnected.Should().BeTrue($"{adapter.ChannelId} must be connected");
            adapter.IsMock.Should().BeTrue($"{adapter.ChannelId} must operate in Mock mode when credentials absent");

            // 3. Outbound message transmission in mock mode
            var msg = AgentMessage.Create(adapter.ChannelId, "test_sender", $"Hello from {adapter.ChannelId}");
            bool msgSent = await adapter.SendMessageAsync(msg);
            msgSent.Should().BeTrue($"{adapter.ChannelId} SendMessageAsync must return true in mock mode");
            adapter.SentMessages.Should().ContainSingle(m => m.Id == msg.Id);

            // 4. Outbound approval prompt in mock mode
            var approval = ApprovalRequest.Create("restart_node", RiskTier.Red, "Emergency reboot");
            bool approvalSent = await adapter.SendApprovalPromptAsync(approval);
            approvalSent.Should().BeTrue($"{adapter.ChannelId} SendApprovalPromptAsync must return true in mock mode");
            adapter.SentApprovals.Should().ContainSingle(a => a.Id == approval.Id);

            // 5. Clean stop
            await adapter.StopAsync();
            adapter.IsConnected.Should().BeFalse($"{adapter.ChannelId} must be disconnected after StopAsync");
        }
    }

    [Theory]
    [InlineData("telegram", "TELEGRAM_BOT_TOKEN", "fake_tok", "TELEGRAM_CHAT_ID", null)]
    [InlineData("discord", "DISCORD_BOT_TOKEN", "fake_tok", "DISCORD_CHANNEL_ID", null)]
    [InlineData("slack", "SLACK_BOT_TOKEN", null, "SLACK_CHANNEL_ID", "fake_chan")]
    [InlineData("whatsapp", "WHATSAPP_API_KEY", "fake_key", "WHATSAPP_PHONE_NUMBER_ID", null)]
    [InlineData("email", "EMAIL_SMTP_HOST", "smtp.mock.com", "EMAIL_PASSWORD", null)]
    public async Task ChannelAdapters_PartialCredentials_StillDegradesToMockMode(
        string adapterName,
        string envKey1, string? envVal1,
        string envKey2, string? envVal2)
    {
        Environment.SetEnvironmentVariable(envKey1, envVal1);
        Environment.SetEnvironmentVariable(envKey2, envVal2);

        IChannelAdapter adapter = adapterName switch
        {
            "telegram" => new TelegramAdapter(),
            "discord" => new DiscordAdapter(),
            "slack" => new SlackAdapter(),
            "whatsapp" => new WhatsAppAdapter(),
            "email" => new EmailAdapter(),
            _ => throw new ArgumentException(adapterName)
        };

        try
        {
            await adapter.StartAsync();
            adapter.IsConnected.Should().BeTrue();
            adapter.IsMock.Should().BeTrue($"Partial credentials for {adapterName} must trigger graceful mock degradation");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey1, null);
            Environment.SetEnvironmentVariable(envKey2, null);
            await adapter.StopAsync();
        }
    }

    [Fact]
    public async Task ChannelAdapters_InboundSimulation_StreamsViaGhostTypingToDesktop()
    {
        var bus = new CentralEventBus();
        var ghostSync = new GhostTypingSync();
        var adapter = new TelegramAdapter(bus, ghostSync);
        await adapter.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedDesktop = new List<AgentMessage>();

        var subTask = Task.Run(async () =>
        {
            await foreach (var m in bus.SubscribeAsync("desktop", cts.Token))
            {
                receivedDesktop.Add(m);
                if (m.Metadata != null && m.Metadata.TryGetValue("triggerAgentReasoning", out string? trig) && trig == "true")
                {
                    break;
                }
            }
        });

        while (bus.SubscriberCount == 0)
        {
            await Task.Delay(10);
        }

        var inbound = AgentMessage.Create("telegram", "@remote_user", "Run /scan");
        adapter.SimulateInboundMessage(inbound);

        await subTask;

        // "Run /scan" is 9 chars -> 9 ghost typing frames + 1 final completion frame = 10 frames
        receivedDesktop.Count.Should().Be(10);
        receivedDesktop.Last().Metadata!["triggerAgentReasoning"].Should().Be("true");
        receivedDesktop.Last().Content.Should().Be("Run /scan");
    }

    // =========================================================================
    // TASK 4: SmartApproval Yellow Grace Cancellation & Red Timeout Expiration
    // =========================================================================

    [Fact]
    public async Task SmartApproval_YellowTier_CancelledExplicitlyDuringGracePeriod_ReturnsDenied()
    {
        var bus = new CentralEventBus();
        var approval = new SmartApproval(bus)
        {
            DefaultYellowGracePeriod = TimeSpan.FromSeconds(3)
        };

        var request = ApprovalRequest.Create("edit_file", RiskTier.Yellow, "Modify config.json");

        var evalTask = approval.RequestApprovalAsync(request);

        // Give the task a moment to register in pending map
        await Task.Delay(40);
        approval.GetPendingRequests().Should().ContainSingle(r => r.Id == request.Id);

        // Explicitly cancel Yellow grace period
        bool cancelled = approval.CancelYellowGracePeriod(request.Id, "Operator cancelled write operation");
        cancelled.Should().BeTrue();

        var decision = await evalTask;

        _output.WriteLine($"[Yellow Cancelled] Decision: IsApproved={decision.IsApproved}, Reason='{decision.Reason}', DecidedBy='{decision.DecidedBy}'");

        decision.IsApproved.Should().BeFalse();
        decision.Tier.Should().Be(RiskTier.Yellow);
        decision.Reason.Should().Contain("Operator cancelled write operation");
        decision.DecidedBy.Should().Be("user:interactive");
        approval.GetPendingRequests().Should().BeEmpty("Pending map must be cleared after resolution");

        // Attempting to cancel again should fail
        approval.CancelYellowGracePeriod(request.Id).Should().BeFalse();
    }

    [Fact]
    public async Task SmartApproval_YellowTier_NotCancelled_AutoApprovesAfterGracePeriod()
    {
        var approval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromMilliseconds(60)
        };

        var request = ApprovalRequest.Create("git_commit", RiskTier.Yellow, "Commit refactor");
        var sw = Stopwatch.StartNew();
        var decision = await approval.RequestApprovalAsync(request);
        sw.Stop();

        _output.WriteLine($"[Yellow Elapsed] Time={sw.ElapsedMilliseconds}ms, Decision: IsApproved={decision.IsApproved}, DecidedBy='{decision.DecidedBy}'");

        decision.IsApproved.Should().BeTrue();
        decision.Tier.Should().Be(RiskTier.Yellow);
        decision.DecidedBy.Should().Contain("yellow-grace-elapsed");
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(50);
        approval.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task SmartApproval_YellowTier_ExternalCancellationToken_AbortsGracePeriod()
    {
        var approval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromSeconds(5)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var request = ApprovalRequest.Create("write_code", RiskTier.Yellow, "Update Program.cs");

        var decision = await approval.RequestApprovalAsync(request, cts.Token);

        _output.WriteLine($"[Yellow Aborted via Token] Decision: IsApproved={decision.IsApproved}, Reason='{decision.Reason}'");
        decision.IsApproved.Should().BeFalse();
        decision.DecidedBy.Should().Be("system:cancelled");
        approval.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task SmartApproval_RedTier_ExternalCancellationToken_AbortsInteractiveRequest()
    {
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromSeconds(120)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var request = ApprovalRequest.Create("delete_database", RiskTier.Red, "Drop db");

        var decision = await approval.RequestApprovalAsync(request, cts.Token);

        _output.WriteLine($"[Red Aborted via Token] Decision: IsApproved={decision.IsApproved}, Reason='{decision.Reason}', DecidedBy='{decision.DecidedBy}'");
        decision.IsApproved.Should().BeFalse();
        decision.DecidedBy.Should().Be("system:cancelled");
        decision.Reason.Should().Contain("cancelled");
        approval.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task SmartApproval_RedTier_TimeoutExpiration_DefaultsStrictlyToDenied()
    {
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromMilliseconds(80)
        };

        var request = ApprovalRequest.Create(
            "delete_database",
            RiskTier.Red,
            "Drop database production_db",
            screenshotBase64: "dGVzdF9zY3JlZW5zaG90"
        );

        var sw = Stopwatch.StartNew();
        var decision = await approval.RequestApprovalAsync(request);
        sw.Stop();

        _output.WriteLine($"[Red Timeout] Elapsed={sw.ElapsedMilliseconds}ms, Decision: IsApproved={decision.IsApproved}, Reason='{decision.Reason}', DecidedBy='{decision.DecidedBy}'");

        decision.IsApproved.Should().BeFalse("Red tier MUST default to DENIED on timeout for security");
        decision.Tier.Should().Be(RiskTier.Red);
        decision.Reason.Should().Contain("timed out");
        decision.Reason.Should().Contain("Defaulting to DENIED for safety");
        decision.DecidedBy.Should().Be("system:red-timeout-denied");
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(70);

        approval.GetPendingRequests().Should().BeEmpty("Timed out requests must be evicted from pending pool");

        // Late approval attempt MUST be rejected
        bool lateApproval = approval.SubmitApproval(request.Id, approved: true);
        lateApproval.Should().BeFalse("Late approvals after timeout MUST be rejected");
    }

    [Fact]
    public async Task SmartApproval_RedTier_ExplicitInteractiveApprovalAndDenial()
    {
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromSeconds(5)
        };

        // Sub-test 1: Explicit Approval
        var reqApprove = ApprovalRequest.Create("format_disk", RiskTier.Red, "Format disk E:");
        var taskApprove = approval.RequestApprovalAsync(reqApprove);
        await Task.Delay(20);

        bool submitApproved = approval.SubmitApproval(reqApprove.Id, approved: true, reason: "Approved by Lead SRE", approvedBy: "user:@alice_sre");
        submitApproved.Should().BeTrue();

        var decApprove = await taskApprove;
        decApprove.IsApproved.Should().BeTrue();
        decApprove.Reason.Should().Be("Approved by Lead SRE");
        decApprove.DecidedBy.Should().Be("user:@alice_sre");

        // Sub-test 2: Explicit Denial
        var reqDeny = ApprovalRequest.Create("shutdown", RiskTier.Red, "Emergency power down");
        var taskDeny = approval.RequestApprovalAsync(reqDeny);
        await Task.Delay(20);

        bool submitDenied = approval.SubmitApproval(reqDeny.Id, approved: false, reason: "Denied by Ops Director", approvedBy: "user:@director");
        submitDenied.Should().BeTrue();

        var decDeny = await taskDeny;
        decDeny.IsApproved.Should().BeFalse();
        decDeny.Reason.Should().Be("Denied by Ops Director");
        decDeny.DecidedBy.Should().Be("user:@director");
    }

    [Fact]
    public async Task SmartApproval_ConcurrentRedRequests_MaintainIsolatedStates()
    {
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromMilliseconds(100)
        };

        var req1 = ApprovalRequest.Create("admin_task_1", RiskTier.Red, "Admin Task 1", customTimeout: TimeSpan.FromSeconds(2));
        var req2 = ApprovalRequest.Create("admin_task_2", RiskTier.Red, "Admin Task 2", customTimeout: TimeSpan.FromSeconds(2));
        var req3 = ApprovalRequest.Create("admin_task_3", RiskTier.Red, "Admin Task 3", customTimeout: TimeSpan.FromMilliseconds(50)); // will time out

        var task1 = approval.RequestApprovalAsync(req1);
        var task2 = approval.RequestApprovalAsync(req2);
        var task3 = approval.RequestApprovalAsync(req3);

        await Task.Delay(20);
        approval.GetPendingRequests().Should().HaveCount(3);

        approval.SubmitApproval(req1.Id, approved: true, "Approved 1");
        approval.SubmitApproval(req2.Id, approved: false, "Denied 2");

        var d1 = await task1;
        var d2 = await task2;
        var d3 = await task3;

        d1.IsApproved.Should().BeTrue();
        d2.IsApproved.Should().BeFalse();
        d3.IsApproved.Should().BeFalse();
        d3.DecidedBy.Should().Be("system:red-timeout-denied");

        approval.GetPendingRequests().Should().BeEmpty();
    }

    [Theory]
    [InlineData("execute_cmd", "rm -rf /home/user", RiskTier.Red)]
    [InlineData("bash_exec", "del /s /q C:\\data", RiskTier.Red)]
    [InlineData("db_query", "drop database secrets", RiskTier.Red)]
    [InlineData("cat_file", "show me .env", RiskTier.Red)]
    [InlineData("ssh_key_access", "read id_rsa", RiskTier.Red)]
    [InlineData("config_dump", "dump api_key and password", RiskTier.Red)]
    [InlineData("checkout_item", "pay $200", RiskTier.Red)]
    [InlineData("write_file", "normal content", RiskTier.Yellow)]
    [InlineData("delete_file", "temp.txt", RiskTier.Yellow)]
    [InlineData("read_file", "doc.txt", RiskTier.Green)]
    [InlineData("som", "", RiskTier.Green)]
    [InlineData("list_windows", "", RiskTier.Green)]
    public void SmartApproval_Classification_DetectsForbiddenPrimitivesAndCommands(
        string action, string payload, RiskTier expectedTier)
    {
        var approval = new SmartApproval();
        var parameters = string.IsNullOrWhiteSpace(payload) ? null : new Dictionary<string, string> { ["input"] = payload };

        var tier = approval.ClassifyAction(action, parameters);
        _output.WriteLine($"[Classification] action='{action}', param='{payload}' -> {tier} (expected {expectedTier})");
        tier.Should().Be(expectedTier);
    }
}
