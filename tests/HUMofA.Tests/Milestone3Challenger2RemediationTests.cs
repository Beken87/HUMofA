using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FluentAssertions;
using HUMofA.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace HUMofA.Tests;

/// <summary>
/// Challenger 2 empirical stress test harness for Milestone 3 Remediation re-gate evaluation.
/// Validates 256-bit CSPRNG token scheme, collision resistance across 10,000 tokens,
/// and adversarial cancellation safety under race conditions and high concurrency.
/// </summary>
public class Milestone3Challenger2RemediationTests
{
    private readonly ITestOutputHelper _output;

    public Milestone3Challenger2RemediationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // =========================================================================
    // 1. 256-Bit CSPRNG Token Entropy & 10,000 Token Collision Resistance
    // =========================================================================

    [Fact]
    public void SmartApproval_TokenGeneration_10000Tokens_AdheresTo256BitSchemeAndZeroCollisions()
    {
        const int totalTokens = 10_000;
        var tokenSet = new ConcurrentDictionary<string, byte>();
        var hexCharCounts = new int[16];
        var hexRegex = new Regex("^appr_[0-9a-f]{64}$", RegexOptions.Compiled);

        Parallel.For(0, totalTokens, i =>
        {
            var req = ApprovalRequest.Create($"action_{i}", RiskTier.Yellow, $"Stress testing token randomness {i}");
            string token = req.Id;

            // 1. Prefix and length: appr_ (5 chars) + 64 hex characters (32 bytes = 256 bits) = 69 chars
            token.Length.Should().Be(69, "Token must be exactly 69 characters long");
            token.StartsWith("appr_").Should().BeTrue("Token must start with 'appr_' prefix");

            // 2. Strict lower-case hex format validation
            hexRegex.IsMatch(token).Should().BeTrue("Token must consist of 'appr_' followed by 64 lower-case hex characters");

            // 3. Collision resistance: insert into concurrent dictionary
            bool added = tokenSet.TryAdd(token, 0);
            added.Should().BeTrue($"Token collision detected on token: {token}");

            // 4. Record hex character distribution for entropy sanity check
            string hexPart = token.Substring(5);
            foreach (char c in hexPart)
            {
                int val = c is >= '0' and <= '9' ? c - '0' : c - 'a' + 10;
                Interlocked.Increment(ref hexCharCounts[val]);
            }
        });

        tokenSet.Count.Should().Be(totalTokens, "All 10,000 tokens must be strictly unique across concurrent generation");
        _output.WriteLine($"[CSPRNG Token Verification] Generated {totalTokens} tokens. Unique tokens: {tokenSet.Count}. Collision rate: 0.00%.");

        // Validate character distribution uniformity across 640,000 hex nibbles
        // Expected mean per nibble = 640,000 / 16 = 40,000
        int totalNibbles = totalTokens * 64;
        double expectedMean = (double)totalNibbles / 16;
        _output.WriteLine($"[Entropy Distribution] Total nibbles: {totalNibbles}, Expected mean per nibble: {expectedMean:F0}");

        for (int nibble = 0; nibble < 16; nibble++)
        {
            int count = hexCharCounts[nibble];
            char charRep = nibble < 10 ? (char)('0' + nibble) : (char)('a' + nibble - 10);
            _output.WriteLine($"Nibble '{charRep}': {count} occurrences (deviation: {(count - expectedMean) / expectedMean * 100:F2}%)");

            // No nibble should deviate by more than 10% from the expected mean in 640,000 random samples
            count.Should().BeInRange((int)(expectedMean * 0.90), (int)(expectedMean * 1.10),
                $"Hex nibble '{charRep}' frequency should be uniformly distributed");
        }
    }

    // =========================================================================
    // 2. Cancellation Safety: Pre-Cancelled Tokens (Immediate Abort)
    // =========================================================================

    [Fact]
    public async Task SmartApproval_YellowTier_PreCancelledToken_AbortsImmediatelyWithDenied()
    {
        var approval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromSeconds(5)
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var req = ApprovalRequest.Create("edit_file", RiskTier.Yellow, "Pre-cancelled Yellow tier");
        var decision = await approval.RequestApprovalAsync(req, cts.Token);

        decision.IsApproved.Should().BeFalse("Pre-cancelled request must not be approved");
        decision.DecidedBy.Should().Be("system:cancelled");
        decision.Reason.Should().Contain("cancelled");
        approval.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task SmartApproval_RedTier_PreCancelledToken_AbortsImmediatelyWithDenied()
    {
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromSeconds(60)
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var req = ApprovalRequest.Create("format_disk", RiskTier.Red, "Pre-cancelled Red tier");
        var decision = await approval.RequestApprovalAsync(req, cts.Token);

        decision.IsApproved.Should().BeFalse("Pre-cancelled request must not be approved");
        decision.DecidedBy.Should().Be("system:cancelled");
        decision.Reason.Should().Contain("cancelled");
        approval.GetPendingRequests().Should().BeEmpty();
    }

    // =========================================================================
    // 3. High-Concurrency Stress Test: Cancellation During In-Flight Processing
    // =========================================================================

    [Fact]
    public async Task SmartApproval_YellowTier_HighConcurrencyCancellationStress_NeverAutoApproves()
    {
        const int concurrentRequests = 100;
        var approval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromMilliseconds(500)
        };

        var decisions = new ConcurrentBag<ApprovalDecision>();

        var tasks = Enumerable.Range(0, concurrentRequests).Select(async i =>
        {
            int cancelDelayMs = (i % 25) + 1; // 1ms to 25ms delay before cancellation
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(cancelDelayMs));

            var req = ApprovalRequest.Create($"write_batch_{i}", RiskTier.Yellow, $"Concurrent batch write {i}");
            var decision = await approval.RequestApprovalAsync(req, cts.Token);
            decisions.Add(decision);
        }).ToArray();

        await Task.WhenAll(tasks);

        decisions.Count.Should().Be(concurrentRequests);
        foreach (var decision in decisions)
        {
            decision.IsApproved.Should().BeFalse("Yellow tier aborted via cancellation must NEVER auto-approve");
            decision.DecidedBy.Should().Be("system:cancelled", "Decision must be attributed to system:cancelled");
            decision.Reason.Should().Contain("cancelled");
        }

        approval.GetPendingRequests().Should().BeEmpty("All pending requests must be cleanly removed from tracking map");
        _output.WriteLine($"[Yellow Concurrency Stress] {concurrentRequests} requests cancelled in flight. Auto-approvals: 0. Cleanly denied: {concurrentRequests}.");
    }

    [Fact]
    public async Task SmartApproval_RedTier_HighConcurrencyCancellationStress_NeverTimesOutOrApproves()
    {
        const int concurrentRequests = 100;
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromMilliseconds(500)
        };

        var decisions = new ConcurrentBag<ApprovalDecision>();

        var tasks = Enumerable.Range(0, concurrentRequests).Select(async i =>
        {
            int cancelDelayMs = (i % 25) + 1; // 1ms to 25ms delay before cancellation
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(cancelDelayMs));

            var req = ApprovalRequest.Create($"admin_batch_{i}", RiskTier.Red, $"Concurrent admin task {i}");
            var decision = await approval.RequestApprovalAsync(req, cts.Token);
            decisions.Add(decision);
        }).ToArray();

        await Task.WhenAll(tasks);

        decisions.Count.Should().Be(concurrentRequests);
        foreach (var decision in decisions)
        {
            decision.IsApproved.Should().BeFalse("Red tier cancelled in flight must NEVER approve");
            decision.DecidedBy.Should().Be("system:cancelled", "Cancelled requests must attribute to system:cancelled, NOT timeout");
            decision.Reason.Should().Contain("cancelled");
        }

        approval.GetPendingRequests().Should().BeEmpty("All pending requests must be cleanly removed from tracking map");
        _output.WriteLine($"[Red Concurrency Stress] {concurrentRequests} requests cancelled in flight. Approvals: 0. Timeouts: 0. Cleanly denied: {concurrentRequests}.");
    }

    // =========================================================================
    // 4. Race Condition: Cancellation vs SubmitApproval (Deterministic State)
    // =========================================================================

    [Fact]
    public async Task SmartApproval_RaceCondition_ConcurrentCancelAndApprove_LeavesNoLeakedState()
    {
        const int rounds = 50;
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromSeconds(2)
        };

        for (int i = 0; i < rounds; i++)
        {
            using var cts = new CancellationTokenSource();
            var req = ApprovalRequest.Create($"race_action_{i}", RiskTier.Red, $"Race test {i}");

            var evalTask = approval.RequestApprovalAsync(req, cts.Token);

            // Barrier to maximize probability of simultaneous arrival
            var barrier = new Barrier(2);

            var cancelTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                cts.Cancel();
            });

            var approveTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                approval.SubmitApproval(req.Id, approved: true, reason: "Approved in race", approvedBy: "user:racer");
            });

            await Task.WhenAll(cancelTask, approveTask);
            var decision = await evalTask;

            // The outcome must be either Approved by user OR Denied by cancellation, never null or invalid
            if (decision.IsApproved)
            {
                decision.DecidedBy.Should().Be("user:racer");
            }
            else
            {
                decision.DecidedBy.Should().Be("system:cancelled");
            }

            approval.GetPendingRequests().Should().NotContain(r => r.Id == req.Id,
                "Request must be evicted from pending dictionary regardless of race outcome");
        }

        approval.GetPendingRequests().Should().BeEmpty();
        _output.WriteLine($"[Race Condition Stress] Completed {rounds} concurrent cancel-vs-approve races without leaked state or exceptions.");
    }

    // =========================================================================
    // 5. Verification of the 2 Previously Failing Challenger 2 Tests
    // =========================================================================

    [Fact]
    public async Task SmartApproval_VerificationOfPreviouslyFailedYellowTest()
    {
        var approval = new SmartApproval
        {
            DefaultYellowGracePeriod = TimeSpan.FromSeconds(5)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var request = ApprovalRequest.Create("write_code", RiskTier.Yellow, "Update Program.cs");

        var decision = await approval.RequestApprovalAsync(request, cts.Token);

        _output.WriteLine($"[Yellow Verified] Decision: IsApproved={decision.IsApproved}, Reason='{decision.Reason}', DecidedBy='{decision.DecidedBy}'");
        decision.IsApproved.Should().BeFalse();
        decision.DecidedBy.Should().Be("system:cancelled");
        decision.Reason.Should().Contain("cancelled");
        approval.GetPendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task SmartApproval_VerificationOfPreviouslyFailedRedTest()
    {
        var approval = new SmartApproval
        {
            DefaultRedTimeout = TimeSpan.FromSeconds(120)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var request = ApprovalRequest.Create("delete_database", RiskTier.Red, "Drop db");

        var decision = await approval.RequestApprovalAsync(request, cts.Token);

        _output.WriteLine($"[Red Verified] Decision: IsApproved={decision.IsApproved}, Reason='{decision.Reason}', DecidedBy='{decision.DecidedBy}'");
        decision.IsApproved.Should().BeFalse();
        decision.DecidedBy.Should().Be("system:cancelled");
        decision.Reason.Should().Contain("cancelled");
        approval.GetPendingRequests().Should().BeEmpty();
    }
}
