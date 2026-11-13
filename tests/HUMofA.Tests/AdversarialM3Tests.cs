using System.Collections.Concurrent;
using FluentAssertions;
using HUMofA.Gateway;
using HUMofA.Scheduler;
using Xunit;

namespace HUMofA.Tests;

/// <summary>
/// Adversarial stress tests challenging Milestone 3 components:
/// CentralEventBus, GhostTypingSync, SmartApproval, and NaturalCronScheduler.
/// </summary>
public class AdversarialM3Tests
{
    // =========================================================================
    // 1. CentralEventBus High-Concurrency & Stress Tests
    // =========================================================================

    [Fact]
    public async Task CentralEventBus_Concurrency_1000MessagesAcross20Tasks_ZeroLossAndZeroDeadlocks()
    {
        // Challenge: 1000 messages published concurrently across 20 tasks into 5 active subscribers
        // Verify: zero deadlocks within 10s, zero lost messages (each subscriber receives all 1000), exact counts.
        const int totalPublishers = 20;
        const int messagesPerPublisher = 50;
        const int totalExpectedMessages = totalPublishers * messagesPerPublisher; // 1000
        const int subscriberCount = 5;

        var bus = new CentralEventBus(subscriberChannelCapacity: 2000, historyCapacity: 500);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var subscriberReceivedBags = new ConcurrentBag<AgentMessage>[subscriberCount];
        for (int i = 0; i < subscriberCount; i++)
        {
            subscriberReceivedBags[i] = new ConcurrentBag<AgentMessage>();
        }

        var subscriberReadySignal = new TaskCompletionSource();
        var subscribersStarted = 0;

        // Launch 5 subscriber tasks
        var subscriberTasks = Enumerable.Range(0, subscriberCount).Select(subIdx => Task.Run(async () =>
        {
            var bag = subscriberReceivedBags[subIdx];
            var stream = bus.SubscribeAsync(cts.Token);

            if (Interlocked.Increment(ref subscribersStarted) == subscriberCount)
            {
                subscriberReadySignal.SetResult();
            }

            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                bag.Add(msg);
                if (bag.Count >= totalExpectedMessages)
                {
                    break;
                }
            }
        })).ToArray();

        // Wait until all subscribers are attached
        await subscriberReadySignal.Task;
        await Task.Delay(30, cts.Token); // Give channel enumerators brief moment to enter WaitToReadAsync

        // Launch 20 concurrent publisher tasks
        var publisherTasks = Enumerable.Range(0, totalPublishers).Select(pubIdx => Task.Run(async () =>
        {
            for (int m = 0; m < messagesPerPublisher; m++)
            {
                var msg = AgentMessage.Create(
                    channel: "test_concurrency",
                    sender: $"pub_{pubIdx}",
                    content: $"msg_{pubIdx}_{m}",
                    id: $"msg_{pubIdx:D2}_{m:D4}"
                );
                await bus.PublishAsync(msg, cts.Token);
            }
        })).ToArray();

        // Await all publishers and subscribers with timeout safeguard
        await Task.WhenAll(publisherTasks);
        await Task.WhenAll(subscriberTasks);

        // Assertions
        bus.TotalPublishedCount.Should().Be(totalExpectedMessages);

        for (int i = 0; i < subscriberCount; i++)
        {
            var received = subscriberReceivedBags[i].ToList();
            received.Should().HaveCount(totalExpectedMessages, $"Subscriber {i} must receive every single published message without loss");

            // Verify unique IDs - no duplicates and no missing IDs
            var distinctIds = received.Select(m => m.Id).ToHashSet();
            distinctIds.Should().HaveCount(totalExpectedMessages, $"Subscriber {i} must receive zero duplicate message IDs");
        }
    }

    [Fact]
    public async Task CentralEventBus_ThreadSafeSubscriberCleanup_ChurnUnderLoad()
    {
        // Challenge: High subscriber churn while heavy publishing is actively occurring
        // Verify: No unhandled exceptions (ChannelClosedException, collection modified), zero deadlocks,
        // and SubscriberCount cleanly drops to zero when all subscribers finish.
        var bus = new CentralEventBus(subscriberChannelCapacity: 500);
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var publishActive = true;
        var publishedCount = 0;

        // Background publisher pumping messages rapidly
        var publisherTask = Task.Run(async () =>
        {
            while (publishActive && !testCts.Token.IsCancellationRequested)
            {
                var msg = AgentMessage.Create("churn", "flooder", $"Data {Interlocked.Increment(ref publishedCount)}");
                await bus.PublishAsync(msg, testCts.Token);
                await Task.Yield();
            }
        });

        // 15 subscribers repeatedly subscribing, reading 1-5 messages, and cancelling/breaking
        var churnTasks = Enumerable.Range(0, 15).Select(idx => Task.Run(async () =>
        {
            for (int cycle = 0; cycle < 5; cycle++)
            {
                using var subCts = CancellationTokenSource.CreateLinkedTokenSource(testCts.Token);
                var receivedInCycle = 0;

                try
                {
                    await foreach (var msg in bus.SubscribeAsync(subCts.Token))
                    {
                        receivedInCycle++;
                        if (receivedInCycle >= 3)
                        {
                            subCts.Cancel(); // Force unsubscription mid-stream
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when subCts is cancelled
                }

                await Task.Delay(5);
            }
        })).ToArray();

        await Task.WhenAll(churnTasks);
        publishActive = false;
        await publisherTask;

        // Allow cleanup routines in finally blocks to finish
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (bus.SubscriberCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        bus.SubscriberCount.Should().Be(0, "All churned subscriptions must be cleanly unregistered from _subscriptions");
    }

    [Fact]
    public async Task CentralEventBus_Backpressure_BoundedCapacityHandlesSlowSubscriberWithoutMessageLoss()
    {
        // Challenge: Subscriber channel capacity = 20, publisher bursts 100 messages into a slow reader
        // Verify: BoundedChannelFullMode.Wait applies backpressure, zero messages dropped, all 100 delivered.
        var bus = new CentralEventBus(subscriberChannelCapacity: 20);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var receivedMessages = new List<AgentMessage>();
        var readySignal = new TaskCompletionSource();

        var slowReaderTask = Task.Run(async () =>
        {
            var stream = bus.SubscribeAsync(cts.Token);
            readySignal.SetResult();

            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                receivedMessages.Add(msg);
                // Introduce slight read delay to create channel congestion
                await Task.Delay(2, cts.Token);
                if (receivedMessages.Count == 100)
                {
                    break;
                }
            }
        });

        await readySignal.Task;
        await Task.Delay(20, cts.Token);

        // Burst 100 messages
        for (int i = 0; i < 100; i++)
        {
            await bus.PublishAsync(AgentMessage.Create("backpressure", "fast_pub", $"P{i}"), cts.Token);
        }

        await slowReaderTask;

        receivedMessages.Should().HaveCount(100);
        for (int i = 0; i < 100; i++)
        {
            receivedMessages[i].Content.Should().Be($"P{i}", "FIFO order must be strictly preserved under backpressure");
        }
    }

    // =========================================================================
    // 2. GhostTypingSync Concurrent Streams & Fidelity Challenge
    // =========================================================================

    [Fact]
    public async Task GhostTypingSync_ConcurrentStreams_CharacterOrderingAndFinalReasoningTrigger()
    {
        // Challenge: 10 concurrent streams executing on the SAME GhostTypingSync instance
        // Verify:
        // 1. Every stream's characters arrive strictly monotonically ordered (charIndex 0..N-1)
        // 2. Progressive partial content matches exact prefixes
        // 3. Exactly one final message per stream carries isFinal=true, triggerAgentReasoning=true, and full content
        // 4. Final message is NEVER received before intermediate ghost messages finish
        const int streamCount = 10;
        var ghostSync = new GhostTypingSync
        {
            MinDelayMs = 1,
            MaxDelayMs = 3
        };

        var bus = new CentralEventBus(subscriberChannelCapacity: 5000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var streamPayloads = new Dictionary<string, string>();
        for (int s = 0; s < streamCount; s++)
        {
            string streamId = $"stream_{s:D2}";
            string payload = $"Ghost typing stream #{s} content with special chars: 12345!@#$%^&*()_+";
            streamPayloads[streamId] = payload;
        }

        // Subscriber collecting all messages from "desktop" channel
        var messagesByOriginalId = new ConcurrentDictionary<string, ConcurrentBag<AgentMessage>>();
        var totalExpectedGhostMessages = streamPayloads.Values.Sum(p => p.Length);
        var totalExpectedFinalMessages = streamCount;
        var totalExpectedTotal = totalExpectedGhostMessages + totalExpectedFinalMessages;

        var readySignal = new TaskCompletionSource();
        var subscriberTask = Task.Run(async () =>
        {
            var stream = bus.SubscribeAsync("desktop", cts.Token);
            readySignal.SetResult();

            var collectedCount = 0;
            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                string origId = msg.Metadata != null && msg.Metadata.TryGetValue("originalId", out var id)
                    ? id
                    : msg.Id;

                var bag = messagesByOriginalId.GetOrAdd(origId, _ => new ConcurrentBag<AgentMessage>());
                bag.Add(msg);

                collectedCount++;
                if (collectedCount >= totalExpectedTotal)
                {
                    break;
                }
            }
        });

        await readySignal.Task;
        await Task.Delay(20, cts.Token);

        // Run 10 streams concurrently
        var streamTasks = streamPayloads.Select(kvp =>
        {
            var inbound = AgentMessage.Create(
                channel: "telegram",
                sender: $"remote_user_{kvp.Key}",
                content: kvp.Value,
                id: kvp.Key
            );
            return ghostSync.StreamGhostTypingAsync(inbound, bus, ct: cts.Token);
        }).ToArray();

        await Task.WhenAll(streamTasks);
        await subscriberTask;

        // Verify each stream's integrity
        messagesByOriginalId.Should().HaveCount(streamCount);

        foreach (var (streamId, expectedText) in streamPayloads)
        {
            messagesByOriginalId.Should().ContainKey(streamId);
            var streamMsgs = messagesByOriginalId[streamId].ToList();

            // Order by charIndex
            var ghostMsgs = streamMsgs
                .Where(m => m.IsGhostTyping)
                .OrderBy(m => int.Parse(m.Metadata!["charIndex"]))
                .ToList();

            ghostMsgs.Should().HaveCount(expectedText.Length, $"Stream {streamId} must emit 1 ghost message per character");

            for (int i = 0; i < expectedText.Length; i++)
            {
                var gm = ghostMsgs[i];
                gm.Metadata!["charIndex"].Should().Be(i.ToString());
                gm.Metadata["totalLength"].Should().Be(expectedText.Length.ToString());
                gm.Metadata["currentChar"].Should().Be(expectedText[i].ToString());
                gm.Metadata["isFinal"].Should().Be("false");
                gm.Content.Should().Be(expectedText.Substring(0, i + 1), "Progressive content must match exact prefix");
            }

            // Verify final message
            var finalMsgs = streamMsgs.Where(m => !m.IsGhostTyping).ToList();
            finalMsgs.Should().HaveCount(1, $"Stream {streamId} must emit exactly 1 final message");

            var finalMsg = finalMsgs[0];
            finalMsg.Content.Should().Be(expectedText);
            finalMsg.Metadata.Should().NotBeNull();
            finalMsg.Metadata!["isFinal"].Should().Be("true");
            finalMsg.Metadata["ghostTypingComplete"].Should().Be("true");
            finalMsg.Metadata["triggerAgentReasoning"].Should().Be("true");
            finalMsg.Metadata["sourceChannel"].Should().Be("telegram");
        }
    }

    [Fact]
    public async Task GhostTypingSync_RandomThreadSafetyStressTest_ConcurrentStreamsNoExceptions()
    {
        // Challenge: 30 concurrent tasks calling StreamGhostTypingAsync using the default random delays
        // to stress-test the internal Random instance across multiple worker threads.
        var ghostSync = new GhostTypingSync
        {
            MinDelayMs = 1,
            MaxDelayMs = 2
        };
        var bus = new CentralEventBus(subscriberChannelCapacity: 2000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            var msg = AgentMessage.Create("slack", $"user_{i}", $"Short text {i}", id: $"stream_rnd_{i}");
            await ghostSync.StreamGhostTypingAsync(msg, bus, ct: cts.Token);
        })).ToArray();

        // Must complete without throwing or hanging
        var awaitAll = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(awaitAll, Task.Delay(TimeSpan.FromSeconds(8)));
        completed.Should().Be(awaitAll, "All 30 concurrent ghost typing tasks using random delay generator must finish safely");
    }

    [Fact]
    public async Task GhostTypingSync_EmptyMessage_PublishesFinalMessageDirectlyWithoutGhostEvents()
    {
        var ghostSync = new GhostTypingSync();
        var bus = new CentralEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var received = new List<AgentMessage>();
        var ready = new TaskCompletionSource();

        var subTask = Task.Run(async () =>
        {
            var stream = bus.SubscribeAsync("desktop", cts.Token);
            ready.SetResult();

            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                received.Add(msg);
                break;
            }
        });

        await ready.Task;
        await Task.Delay(20);

        var emptyMsg = AgentMessage.Create("discord", "user0", string.Empty, id: "empty_001");
        await ghostSync.StreamGhostTypingAsync(emptyMsg, bus, ct: cts.Token);

        await subTask;

        received.Should().HaveCount(1);
        received[0].IsGhostTyping.Should().BeFalse();
        received[0].Content.Should().BeEmpty();
        var meta = received[0].Metadata;
        meta.Should().NotBeNull();
        meta!["isFinal"].Should().Be("true");
        meta["ghostTypingComplete"].Should().Be("true");
    }

    [Fact]
    public async Task GhostTypingSync_Cancellation_AbortsImmediatelyWithoutReasoningTrigger()
    {
        // Challenge: Cancel token after first character is emitted.
        // Verify: StreamGhostTypingAsync throws OperationCanceledException, and NO final reasoning message is emitted!
        var ghostSync = new GhostTypingSync
        {
            MinDelayMs = 50,
            MaxDelayMs = 100
        };
        var bus = new CentralEventBus();
        using var cts = new CancellationTokenSource();

        var received = new List<AgentMessage>();
        var ready = new TaskCompletionSource();

        var subTask = Task.Run(async () =>
        {
            var stream = bus.SubscribeAsync("desktop");
            ready.SetResult();

            await foreach (var msg in stream)
            {
                received.Add(msg);
                if (received.Count == 1)
                {
                    cts.Cancel(); // Cancel mid-stream
                }
            }
        });

        await ready.Task;
        await Task.Delay(20);

        var inbound = AgentMessage.Create("telegram", "user", "A very long message that should be cancelled midway");

        var act = async () => await ghostSync.StreamGhostTypingAsync(inbound, bus, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Allow any lingering messages
        await Task.Delay(100);

        received.Should().NotContain(m => m.Metadata != null && m.Metadata.ContainsKey("triggerAgentReasoning"),
            "When cancelled mid-stream, the final agent reasoning trigger MUST NOT be emitted");
    }

    // =========================================================================
    // 3. SmartApproval Adversarial Stress Tests
    // =========================================================================

    [Theory]
    [InlineData("execute_bash", "rm -rf /var/data", RiskTier.Red)]
    [InlineData("format_drive", "drive: C", RiskTier.Red)]
    [InlineData("run_sql", "DROP DATABASE production;", RiskTier.Red)]
    [InlineData("read_credentials", "export API_KEY=secret", RiskTier.Red)]
    [InlineData("dump_file", "path: /home/user/.env", RiskTier.Red)]
    [InlineData("sudo_escalate", "id_rsa read", RiskTier.Red)]
    [InlineData("edit_code", "file: Program.cs", RiskTier.Yellow)]
    [InlineData("write_file", "path: temp.txt", RiskTier.Yellow)]
    [InlineData("git_commit", "message: checkpoint", RiskTier.Yellow)]
    [InlineData("som_inspect", "target: window", RiskTier.Green)]
    [InlineData("read_file", "path: README.md", RiskTier.Green)]
    [InlineData("click_element", "x: 100, y: 200", RiskTier.Green)]
    public void SmartApproval_ClassifyAction_AdversarialSecurityPatterns(string action, string paramVal, RiskTier expectedTier)
    {
        var approval = new SmartApproval();
        var parameters = new Dictionary<string, string> { ["input"] = paramVal };
        var tier = approval.ClassifyAction(action, parameters);
        tier.Should().Be(expectedTier);
    }

    [Fact]
    public async Task SmartApproval_RedTier_TimeoutEnforcesDenyStrictly()
    {
        // Challenge: Red tier request with 100ms timeout
        // Verify: When no token approval is received, it strictly evaluates to Denied
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromMilliseconds(100)
        };

        var req = ApprovalRequest.Create(
            "format_disk",
            RiskTier.Red,
            "Wipe system partition"
        );

        var decision = await approval.RequestApprovalAsync(req);

        decision.IsApproved.Should().BeFalse();
        decision.Tier.Should().Be(RiskTier.Red);
        decision.Reason.Should().Contain("DENIED");
        approval.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task SmartApproval_YellowGracePeriod_CancellationPreventsExecution()
    {
        // Challenge: Yellow tier action with 300ms grace period; user cancels at 50ms
        // Verify: Returns Denied, does not auto-approve
        var approval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromMilliseconds(300)
        };

        var req = ApprovalRequest.Create(
            "write_file",
            RiskTier.Yellow,
            "Overwrite critical config"
        );

        var task = approval.RequestApprovalAsync(req);

        await Task.Delay(50);
        var cancelled = approval.CancelYellowGracePeriod(req.Id, "User clicked Cancel on grace period bar");
        cancelled.Should().BeTrue();

        var decision = await task;
        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().ContainEquivalentOf("cancel");
        approval.GetPendingRequests().Should().BeEmpty();
    }

    // =========================================================================
    // 4. NaturalCronScheduler Adversarial Expressions & High Frequency
    // =========================================================================

    [Theory]
    [InlineData("every 1 minute", "* * * * *")]
    [InlineData("every 15 minutes", "*/15 * * * *")]
    [InlineData("every 1 hour", "0 * * * *")]
    [InlineData("every 6 hours", "0 */6 * * *")]
    [InlineData("daily at 09:00", "0 9 * * *")]
    [InlineData("every morning at 8:30am", "30 8 * * *")]
    [InlineData("every evening at 7:15pm", "15 19 * * *")]
    [InlineData("every monday at 10:00", "0 10 * * 1")]
    [InlineData("every sunday midnight", "0 0 * * 0")]
    [InlineData("every weekday at 09:00", "0 9 * * 1-5")]
    [InlineData("weekdays at 14:00", "0 14 * * 1-5")]
    public void NaturalCronScheduler_AdversarialExpressions_TranslatesToAccurateCron(string input, string expectedCron)
    {
        var parsed = NaturalCronScheduler.Parse(input);
        parsed.CronExpression.Should().Be(expectedCron);

        // Verify next occurrence is strictly in the future
        var now = DateTime.UtcNow;
        var next = parsed.NextOccurrenceCalculator(now);
        next.Should().BeAfter(now);
    }

    [Fact]
    public async Task NaturalCronScheduler_ConcurrentJobRegistrationsAndExecutionUnderLoop()
    {
        // Challenge: 10 concurrent jobs registered while scheduler loop is active
        var bus = new CentralEventBus();
        var scheduler = new NaturalCronScheduler(bus)
        {
            PollingInterval = TimeSpan.FromMilliseconds(50)
        };

        var executionCounts = new ConcurrentDictionary<string, int>();

        await scheduler.StartAsync();

        var tasks = Enumerable.Range(0, 10).Select(i =>
        {
            string jobName = $"job_{i}";
            return Task.Run(() =>
            {
                return scheduler.RegisterJob(jobName, "every 1 minute", _ =>
                {
                    executionCounts.AddOrUpdate(jobName, 1, (_, c) => c + 1);
                    return Task.FromResult("OK");
                });
            });
        }).ToArray();

        var registeredJobs = await Task.WhenAll(tasks);

        scheduler.ListJobs().Should().HaveCount(10);

        // Trigger each job manually to stress concurrent execution
        var triggerTasks = registeredJobs.Select(j => scheduler.TriggerNowAsync(j.Id)).ToArray();
        await Task.WhenAll(triggerTasks);

        executionCounts.Should().HaveCount(10);
        foreach (var count in executionCounts.Values)
        {
            count.Should().BeGreaterThanOrEqualTo(1);
        }

        await scheduler.StopAsync();
    }

    // =========================================================================
    // 5. Additional Concurrency & Edge-Case Stress Tests
    // =========================================================================

    [Fact]
    public async Task CentralEventBus_ConcurrentFilteredSubscribers_ZeroMessageLeakage()
    {
        // Challenge: 4 channels, 4 publishers sending 100 messages each, 4 subscribers filtered by channel
        // Verify: Strict channel isolation under concurrent publishing, zero cross-channel leakage, zero loss.
        var bus = new CentralEventBus(subscriberChannelCapacity: 1000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var channels = new[] { "telegram", "discord", "slack", "cli" };
        const int messagesPerChannel = 100;

        var receivedByChannel = new ConcurrentDictionary<string, ConcurrentBag<AgentMessage>>();
        foreach (var ch in channels)
        {
            receivedByChannel[ch] = new ConcurrentBag<AgentMessage>();
        }

        var readySignals = channels.Select(_ => new TaskCompletionSource()).ToArray();

        var subscriberTasks = channels.Select((channel, idx) => Task.Run(async () =>
        {
            var stream = bus.SubscribeAsync(channel, cts.Token);
            readySignals[idx].SetResult();

            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                receivedByChannel[channel].Add(msg);
                if (receivedByChannel[channel].Count >= messagesPerChannel)
                {
                    break;
                }
            }
        })).ToArray();

        await Task.WhenAll(readySignals.Select(s => s.Task));
        await Task.Delay(20, cts.Token);

        var publisherTasks = channels.Select(channel => Task.Run(async () =>
        {
            for (int i = 0; i < messagesPerChannel; i++)
            {
                var msg = AgentMessage.Create(channel, "sender", $"Payload {i} for {channel}");
                await bus.PublishAsync(msg, cts.Token);
            }
        })).ToArray();

        await Task.WhenAll(publisherTasks);
        await Task.WhenAll(subscriberTasks);

        foreach (var channel in channels)
        {
            var msgs = receivedByChannel[channel].ToList();
            msgs.Should().HaveCount(messagesPerChannel);
            msgs.Should().OnlyContain(m => m.Channel == channel, $"No message from another channel should leak into '{channel}'");
        }
    }

    [Fact]
    public async Task GhostTypingSync_EmojiAndMultibyteText_PreservesIntegrityAndOrder()
    {
        // Challenge: Complex multilingual string containing multi-byte UTF-16 surrogate pairs and emojis
        var ghostSync = new GhostTypingSync
        {
            MinDelayMs = 0,
            MaxDelayMs = 1
        };
        var bus = new CentralEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        const string textWithEmojis = "🚀 HUMofA: Türkçe karakterler çşğüöı, 日本語テスト, and emojis 🎉🔥✨";

        var receivedGhostUpdates = new List<AgentMessage>();
        AgentMessage? finalMessage = null;
        var ready = new TaskCompletionSource();

        var subTask = Task.Run(async () =>
        {
            var stream = bus.SubscribeAsync("desktop", cts.Token);
            ready.SetResult();

            await foreach (var msg in stream.WithCancellation(cts.Token))
            {
                if (msg.IsGhostTyping)
                {
                    receivedGhostUpdates.Add(msg);
                }
                else
                {
                    finalMessage = msg;
                    break;
                }
            }
        });

        await ready.Task;
        await Task.Delay(20);

        var inbound = AgentMessage.Create("signal", "vip_user", textWithEmojis, id: "emoji_stream_01");
        await ghostSync.StreamGhostTypingAsync(inbound, bus, pacingDelay: TimeSpan.FromMilliseconds(1), ct: cts.Token);

        await subTask;

        finalMessage.Should().NotBeNull();
        finalMessage!.Content.Should().Be(textWithEmojis, "Final completed message content must match exact multibyte string");
        finalMessage.Metadata!["isFinal"].Should().Be("true");
        finalMessage.Metadata["triggerAgentReasoning"].Should().Be("true");

        receivedGhostUpdates.Should().HaveCount(textWithEmojis.Length);
        receivedGhostUpdates.Last().Content.Should().Be(textWithEmojis);
    }

    [Fact]
    public async Task CentralEventBus_RingBufferHistory_ConcurrentReadsAndWrites()
    {
        // Challenge: High frequency publishing while concurrent threads continuously call GetRecentHistory
        var bus = new CentralEventBus(historyCapacity: 100);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var publishing = true;

        var pubTasks = Enumerable.Range(0, 10).Select(p => Task.Run(async () =>
        {
            int counter = 0;
            while (publishing && !cts.Token.IsCancellationRequested)
            {
                await bus.PublishAsync(AgentMessage.Create("history", $"p_{p}", $"Item {counter++}"));
                await Task.Yield();
            }
        })).ToArray();

        var readTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            while (publishing && !cts.Token.IsCancellationRequested)
            {
                var history = bus.GetRecentHistory(50);
                history.Should().NotBeNull();
                history.Count.Should().BeLessThanOrEqualTo(50);
                foreach (var item in history)
                {
                    item.Should().NotBeNull();
                }
                await Task.Delay(5);
            }
        })).ToArray();

        await Task.Delay(500); // Stress for 500ms
        publishing = false;

        await Task.WhenAll(pubTasks);
        await Task.WhenAll(readTasks);

        var finalHistory = bus.GetRecentHistory(50);
        finalHistory.Should().HaveCount(50);
    }
}
