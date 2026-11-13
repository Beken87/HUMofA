using FluentAssertions;
using HUMofA.Gateway;
using HUMofA.Scheduler;
using Xunit;

namespace HUMofA.Tests;

public class SchedulerTests
{
    [Theory]
    [InlineData("every 5 minutes", "*/5 * * * *")]
    [InlineData("every 15 minutes", "*/15 * * * *")]
    [InlineData("every minute", "* * * * *")]
    public void NaturalCronScheduler_Parse_MinutesExpressions_TranslatesToCron(string expression, string expectedCron)
    {
        var parsed = NaturalCronScheduler.Parse(expression);
        parsed.CronExpression.Should().Be(expectedCron);

        var refTime = new DateTime(2026, 11, 2, 10, 2, 30, DateTimeKind.Utc);
        var next = parsed.NextOccurrenceCalculator(refTime);

        next.Should().BeAfter(refTime);
        if (expectedCron.StartsWith("*/5"))
        {
            next.Minute.Should().Be(5);
        }
        else if (expectedCron.StartsWith("*/15"))
        {
            next.Minute.Should().Be(15);
        }
        else
        {
            next.Minute.Should().Be(3);
        }
    }

    [Theory]
    [InlineData("every 2 hours", "0 */2 * * *")]
    [InlineData("every hour", "0 * * * *")]
    public void NaturalCronScheduler_Parse_HoursExpressions_TranslatesToCron(string expression, string expectedCron)
    {
        var parsed = NaturalCronScheduler.Parse(expression);
        parsed.CronExpression.Should().Be(expectedCron);

        var refTime = new DateTime(2026, 11, 2, 10, 45, 0, DateTimeKind.Utc);
        var next = parsed.NextOccurrenceCalculator(refTime);

        next.Should().BeAfter(refTime);
        next.Minute.Should().Be(0);
        if (expectedCron.Contains("*/2"))
        {
            next.Hour.Should().Be(12);
        }
        else
        {
            next.Hour.Should().Be(11);
        }
    }

    [Theory]
    [InlineData("daily at 09:00", "0 9 * * *")]
    [InlineData("daily at 14:30", "30 14 * * *")]
    [InlineData("every morning at 09:00", "0 9 * * *")]
    [InlineData("every morning at 9am", "0 9 * * *")]
    [InlineData("every evening at 6pm", "0 18 * * *")]
    public void NaturalCronScheduler_Parse_DailyExpressions_TranslatesToCron(string expression, string expectedCron)
    {
        var parsed = NaturalCronScheduler.Parse(expression);
        parsed.CronExpression.Should().Be(expectedCron);

        // Before 9:00 -> should trigger same day at 09:00
        var beforeTime = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        var next1 = parsed.NextOccurrenceCalculator(beforeTime);
        next1.Should().BeAfter(beforeTime);

        // After target time -> should trigger next day
        var afterTime = new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);
        var next2 = parsed.NextOccurrenceCalculator(afterTime);
        next2.Should().BeAfter(afterTime);
        next2.Day.Should().Be(3);
    }

    [Theory]
    [InlineData("every monday at 10:00", "0 10 * * 1")]
    [InlineData("every friday at 17:00", "0 17 * * 5")]
    [InlineData("every sunday midnight", "0 0 * * 0")]
    public void NaturalCronScheduler_Parse_DayOfWeekExpressions_TranslatesToCron(string expression, string expectedCron)
    {
        var parsed = NaturalCronScheduler.Parse(expression);
        parsed.CronExpression.Should().Be(expectedCron);

        var refTime = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc); // Monday
        var next = parsed.NextOccurrenceCalculator(refTime);

        next.Should().BeAfter(refTime);
        if (expectedCron.EndsWith(" 1"))
        {
            next.DayOfWeek.Should().Be(DayOfWeek.Monday);
            next.Hour.Should().Be(10);
            next.Minute.Should().Be(0);
        }
        else if (expectedCron.EndsWith(" 5"))
        {
            next.DayOfWeek.Should().Be(DayOfWeek.Friday);
            next.Hour.Should().Be(17);
        }
        else if (expectedCron.EndsWith(" 0"))
        {
            next.DayOfWeek.Should().Be(DayOfWeek.Sunday);
            next.Hour.Should().Be(0);
            next.Minute.Should().Be(0);
        }
    }

    [Fact]
    public void NaturalCronScheduler_Parse_Weekdays_TranslatesToCron()
    {
        var parsed = NaturalCronScheduler.Parse("every weekday at 09:00");
        parsed.CronExpression.Should().Be("0 9 * * 1-5");

        // Friday afternoon
        var fridayEvening = new DateTime(2026, 11, 6, 18, 0, 0, DateTimeKind.Utc);
        var next = parsed.NextOccurrenceCalculator(fridayEvening);

        next.Should().BeAfter(fridayEvening);
        next.DayOfWeek.Should().Be(DayOfWeek.Monday);
        next.Hour.Should().Be(9);
        next.Minute.Should().Be(0);
    }

    [Fact]
    public void NaturalCronScheduler_Parse_StandardCron_TranslatesCorrectly()
    {
        var parsed = NaturalCronScheduler.Parse("*/10 * * * *");
        parsed.CronExpression.Should().Be("*/10 * * * *");

        var refTime = new DateTime(2026, 11, 2, 14, 3, 0, DateTimeKind.Utc);
        var next = parsed.NextOccurrenceCalculator(refTime);

        next.Minute.Should().Be(10);
    }

    [Fact]
    public void NaturalCronScheduler_Parse_InvalidExpression_ThrowsArgumentException()
    {
        var act = () => NaturalCronScheduler.Parse("whenever you feel like it");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unable to parse*");
    }

    [Fact]
    public void NaturalCronScheduler_RegisterAndListJobs_MaintainsActiveState()
    {
        var scheduler = new NaturalCronScheduler();

        var job1 = scheduler.RegisterJob("Backup DB", "daily at 02:00", _ => Task.FromResult("Done"));
        var job2 = scheduler.RegisterJob("Ping Health", "every 5 minutes", _ => Task.FromResult("Pong"));

        var jobs = scheduler.ListJobs();
        jobs.Should().HaveCount(2);

        job1.Status.Should().Be(CronJobStatus.Active);
        job1.NextRun.Should().NotBeNull();
        job2.Status.Should().Be(CronJobStatus.Active);
        job2.NextRun.Should().NotBeNull();
    }

    [Fact]
    public void NaturalCronScheduler_PauseAndResumeJob_UpdatesStatus()
    {
        var scheduler = new NaturalCronScheduler();
        var job = scheduler.RegisterJob("Health Check", "every minute", _ => Task.FromResult("OK"));

        bool paused = scheduler.PauseJob(job.Id);
        paused.Should().BeTrue();
        job.Status.Should().Be(CronJobStatus.Paused);

        bool resumed = scheduler.ResumeJob(job.Id);
        resumed.Should().BeTrue();
        job.Status.Should().Be(CronJobStatus.Active);
        job.NextRun.Should().NotBeNull();
    }

    [Fact]
    public void NaturalCronScheduler_CancelJob_SetsStatusCancelled()
    {
        var scheduler = new NaturalCronScheduler();
        var job = scheduler.RegisterJob("Temp Task", "every 2 hours", _ => Task.FromResult("Done"));

        bool cancelled = scheduler.CancelJob(job.Id);
        cancelled.Should().BeTrue();
        job.Status.Should().Be(CronJobStatus.Cancelled);
    }

    [Fact]
    public async Task NaturalCronScheduler_TriggerNowAsync_ExecutesActionAndUpdatesStats()
    {
        var scheduler = new NaturalCronScheduler();
        int executionCounter = 0;

        var job = scheduler.RegisterJob("Reconcile Task", "daily at 09:00", _ =>
        {
            executionCounter++;
            return Task.FromResult($"Reconciled {executionCounter} items");
        });

        string result = await scheduler.TriggerNowAsync(job.Id);

        result.Should().Be("Reconciled 1 items");
        job.RunCount.Should().Be(1);
        job.LastRun.Should().NotBeNull();
        job.LastResult.Should().Be("Reconciled 1 items");
        job.Status.Should().Be(CronJobStatus.Active);
    }

    [Fact]
    public async Task NaturalCronScheduler_TriggerNowAsync_ReportsToEventBus()
    {
        var bus = new CentralEventBus();
        var scheduler = new NaturalCronScheduler(bus);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        AgentMessage? report = null;

        var subTask = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync("scheduler", cts.Token))
            {
                report = msg;
                break;
            }
        });

        await Task.Delay(20);

        var job = scheduler.RegisterJob("Report Job", "every hour", _ => Task.FromResult("Report generated"));
        await scheduler.TriggerNowAsync(job.Id, cts.Token);

        await subTask;

        report.Should().NotBeNull();
        report!.Channel.Should().Be("scheduler");
        report.Content.Should().Contain("Report generated");
        report.Metadata!["jobId"].Should().Be(job.Id);
        report.Metadata["runCount"].Should().Be("1");
    }

    [Fact]
    public async Task NaturalCronScheduler_ActionThrowsException_CapturesErrorGracefully()
    {
        var scheduler = new NaturalCronScheduler();
        var job = scheduler.RegisterJob("Failing Job", "every 5 minutes", _ => throw new InvalidOperationException("DB connection failed"));

        string result = await scheduler.TriggerNowAsync(job.Id);

        result.Should().Contain("DB connection failed");
        job.LastResult.Should().Contain("DB connection failed");
        job.RunCount.Should().Be(1);
        job.Status.Should().Be(CronJobStatus.Active);
    }

    [Fact]
    public async Task NaturalCronScheduler_BackgroundLoop_ExecutesDueJobs()
    {
        var scheduler = new NaturalCronScheduler
        {
            PollingInterval = TimeSpan.FromMilliseconds(20)
        };

        var executedTcs = new TaskCompletionSource<bool>();

        var job = scheduler.RegisterJob("Immediate Due Job", "every minute", _ =>
        {
            executedTcs.TrySetResult(true);
            return Task.FromResult("Ran automatically in background");
        });

        // Set NextRun to past to make it immediately due
        job.NextRun = DateTime.UtcNow.AddSeconds(-5);

        await scheduler.StartAsync();

        var completed = await Task.WhenAny(executedTcs.Task, Task.Delay(2000));
        await scheduler.StopAsync();

        completed.Should().Be(executedTcs.Task, "The background scheduler loop should have detected and executed the due job.");
        job.RunCount.Should().BeGreaterThanOrEqualTo(1);
        job.LastResult.Should().Be("Ran automatically in background");
    }
}
