using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HUMofA.ComputerUse;
using Xunit;

namespace HUMofA.Tests;

public class Milestone2ChallengerTests
{
    // =========================================================================
    // Challenge 1: PanicKillSwitch Latency Benchmark & Stress Harness
    // =========================================================================

    [Fact]
    public void PanicKillSwitch_100Iterations_EveryIterationSatisfiesSub10MsLatency()
    {
        var mock = new MockInputSimulator();
        using var killSwitch = new PanicKillSwitch(mock, installHooks: false);

        var latencies = new List<double>(100);
        var stopwatchLatencies = new List<double>(100);

        for (int i = 0; i < 100; i++)
        {
            killSwitch.Reset();
            killSwitch.IsPanicTriggered.Should().BeFalse($"Reset must clear panic state at iteration {i}");

            var sw = Stopwatch.StartNew();
            double internalLatencyMs = killSwitch.TriggerPanic($"Adversarial Iteration {i}");
            sw.Stop();

            double externalLatencyMs = sw.Elapsed.TotalMilliseconds;

            killSwitch.IsPanicTriggered.Should().BeTrue($"Kill switch must be triggered at iteration {i}");
            killSwitch.CancellationToken.IsCancellationRequested.Should().BeTrue($"CancellationToken must be cancelled at iteration {i}");

            // STRICT ADVERSARIAL ASSERTION: Every single iteration must be <= 10.0 ms
            internalLatencyMs.Should().BeLessThanOrEqualTo(10.0,
                $"Internal latency at iteration {i} must satisfy <= 10ms (was {internalLatencyMs:F4}ms)");
            externalLatencyMs.Should().BeLessThanOrEqualTo(10.0,
                $"External latency at iteration {i} must satisfy <= 10ms (was {externalLatencyMs:F4}ms)");

            latencies.Add(internalLatencyMs);
            stopwatchLatencies.Add(externalLatencyMs);
        }

        // Statistical validation across the 100 iterations
        double avgInternal = latencies.Average();
        double maxInternal = latencies.Max();
        double avgExternal = stopwatchLatencies.Average();
        double maxExternal = stopwatchLatencies.Max();

        avgInternal.Should().BeLessThan(1.0, $"Average internal latency should be sub-millisecond (was {avgInternal:F4}ms)");
        maxInternal.Should().BeLessThanOrEqualTo(10.0, $"Max internal latency across 100 iterations must be <= 10ms (was {maxInternal:F4}ms)");
        maxExternal.Should().BeLessThanOrEqualTo(10.0, $"Max external latency across 100 iterations must be <= 10ms (was {maxExternal:F4}ms)");
    }

    [Fact]
    public void PanicKillSwitch_ConcurrentMultiThreadedTrigger_RemainsSub10MsAndThreadSafe()
    {
        var mock = new MockInputSimulator();
        using var killSwitch = new PanicKillSwitch(mock, installHooks: false);

        int threadCount = 10;
        var latencies = new ConcurrentBag<double>();
        using var startEvent = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, threadCount).Select(threadId => new Thread(() =>
        {
            startEvent.Wait();
            var sw = Stopwatch.StartNew();
            killSwitch.TriggerPanic($"Concurrent Thread {threadId}");
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
        })).ToList();

        threads.ForEach(t => t.Start());
        startEvent.Set();
        threads.ForEach(t => t.Join());

        killSwitch.IsPanicTriggered.Should().BeTrue();
        killSwitch.CancellationToken.IsCancellationRequested.Should().BeTrue();
        latencies.Count.Should().Be(threadCount);

        foreach (var latency in latencies)
        {
            latency.Should().BeLessThanOrEqualTo(10.0, $"Concurrent trigger latency must remain <= 10ms (was {latency:F4}ms)");
        }
    }

    [Theory]
    [InlineData(100, 100, 0, 1000, 105, 100, 0, 1100, false)] // 50 px/s -> No panic
    [InlineData(100, 100, 0, 1000, 150, 100, 0, 1100, true)]  // 500 px/s physical -> Panic!
    [InlineData(100, 100, 0, 1000, 500, 500, Win32Native.LLMHF_INJECTED, 1020, false)] // 28,000 px/s INJECTED -> No panic
    [InlineData(0, 0, 0, 1000, 30, 0, 0, 1100, true)] // 300 px/s physical -> Panic!
    public void PanicKillSwitch_VelocityDetection_DiscriminatesPhysicalVsInjected(
        int x1, int y1, uint f1, uint t1,
        int x2, int y2, uint f2, uint t2,
        bool expectPanic)
    {
        var mock = new MockInputSimulator();
        using var killSwitch = new PanicKillSwitch(mock, installHooks: false);

        killSwitch.ProcessMouseEvent(x1, y1, f1, t1);
        bool triggered = killSwitch.ProcessMouseEvent(x2, y2, f2, t2);

        triggered.Should().Be(expectPanic);
        killSwitch.IsPanicTriggered.Should().Be(expectPanic);
    }

    // =========================================================================
    // Challenge 2: BezierMath Mathematical Trajectories & Extreme Distance Harness
    // =========================================================================

    [Theory]
    [InlineData(0, 0, 0, 0)]                // 0px distance (same origin and target)
    [InlineData(500, 500, 500, 500)]        // 0px distance at non-zero offset
    [InlineData(100, 100, 101, 100)]        // 1px horizontal distance
    [InlineData(100, 100, 100, 101)]        // 1px vertical distance
    [InlineData(0, 0, 5000, 5000)]          // 5000px extreme diagonal
    [InlineData(-2500, -2500, 2500, 2500)]  // 7071px spanning negative multi-monitor space
    [InlineData(0, 0, 3840, 2160)]          // 4K screen: Top-Left to Bottom-Right
    [InlineData(3840, 0, 0, 2160)]          // 4K screen: Top-Right to Bottom-Left
    [InlineData(3840, 2160, 0, 0)]          // 4K screen: Bottom-Right to Top-Left
    [InlineData(0, 2160, 3840, 0)]          // 4K screen: Bottom-Left to Top-Right
    [InlineData(-1920, -1080, 1920, 1080)]  // Dual-monitor span across negative boundary
    public void BezierMath_ExtremeDistances_ProducesValidTrajectoryAndEndpointConvergence(
        double x0, double y0, double x3, double y3)
    {
        var p0 = new Point2D(x0, y0);
        var p3 = new Point2D(x3, y3);

        var trajectory = BezierMath.GenerateTrajectory(
            p0, p3, targetWidth: 40.0, addMicroTremor: true, tremorMaxSigma: 0.7);

        // 1. Must produce a non-empty list of points
        trajectory.Should().NotBeEmpty();
        trajectory.Count.Should().BeGreaterThanOrEqualTo(2);

        // 2. No NaN or Infinity in any coordinate
        for (int i = 0; i < trajectory.Count; i++)
        {
            double px = trajectory[i].X;
            double py = trajectory[i].Y;

            double.IsNaN(px).Should().BeFalse($"Trajectory point [{i}].X must not be NaN");
            double.IsNaN(py).Should().BeFalse($"Trajectory point [{i}].Y must not be NaN");
            double.IsInfinity(px).Should().BeFalse($"Trajectory point [{i}].X must not be Infinity");
            double.IsInfinity(py).Should().BeFalse($"Trajectory point [{i}].Y must not be Infinity");
        }

        // 3. Start point should be within micro-tremor bounds (3 * sigma = ~2.1px + tolerance) of p0
        trajectory[0].X.Should().BeApproximately(p0.X, 5.0, "Start point must be within micro-tremor of P0");
        trajectory[0].Y.Should().BeApproximately(p0.Y, 5.0, "Start point must be within micro-tremor of P0");

        // 4. Exact endpoint convergence guarantee at final step
        trajectory[^1].X.Should().Be(p3.X, "Final trajectory point must EXACTLY equal target P3.X");
        trajectory[^1].Y.Should().Be(p3.Y, "Final trajectory point must EXACTLY equal target P3.Y");
    }

    [Fact]
    public void BezierMath_MinimumJerk_HighResolution_MonotonicityAndBoundaryProof()
    {
        int subdivisions = 10000;
        double previous = 0.0;

        BezierMath.MinimumJerk(0.0).Should().Be(0.0);
        BezierMath.MinimumJerk(1.0).Should().Be(1.0);

        for (int i = 0; i <= subdivisions; i++)
        {
            double tau = (double)i / subdivisions;
            double current = BezierMath.MinimumJerk(tau);

            // Bounded in [0, 1]
            current.Should().BeInRange(0.0, 1.0, $"MinimumJerk({tau}) must stay bounded within [0, 1]");

            // Monotonic non-decreasing
            current.Should().BeGreaterOrEqualTo(previous,
                $"MinimumJerk must be strictly monotonic at step {i} (tau={tau})");

            previous = current;
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(100.0)]
    [InlineData(10000.0)]
    [InlineData(1000000.0)]
    public void BezierMath_FittsLaw_AlwaysBoundedWithinMinMaxDuration(double distance)
    {
        double duration = BezierMath.CalculateFittsDuration(distance, targetWidth: 40.0);
        duration.Should().BeInRange(BezierMath.MinMovementDuration, BezierMath.MaxMovementDuration,
            $"Fitts duration for distance {distance} must be clamped to [{BezierMath.MinMovementDuration}, {BezierMath.MaxMovementDuration}]");
    }

    // =========================================================================
    // Challenge 3: HumanizedController Simulated Typo & Backspace Reconstruction
    // =========================================================================

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog", 0.05, 1111)]
    [InlineData("HUMofA Platform 2026: Autonomous Humanized Desktop Control!", 0.10, 2222)]
    [InlineData("Mississippi Bookkeeper Committee 99.9% Reliable & Secure", 0.20, 3333)]
    [InlineData("Hello, World! 12345: (Testing Typo Injection & Backspace...)", 0.50, 4444)]
    [InlineData("hello world test typing sequence", 1.00, 5555)] // 100% typo attempt on all eligible chars
    [InlineData("A", 1.00, 6666)]
    [InlineData("Z", 1.00, 7777)]
    [InlineData("1234567890", 1.00, 8888)] // Digits (no Qwerty neighbors in dictionary)
    public async Task HumanizedController_TypoInjectionAndCorrection_ReconstructsExactOriginalSequence(
        string input, double typoProbability, int seed)
    {
        var mock = new MockInputSimulator();
        var rng = new Random(seed);
        var controller = new HumanizedController(mock, rng: rng) { FastModeForTesting = true };

        await controller.TypeText(input, humanize: false, injectTypos: true, typoProbability: typoProbability);

        // Reconstruct the text buffer by simulating how an OS or text editor processes key events
        var buffer = new List<char>();

        foreach (var ev in mock.Events)
        {
            if (ev.EventType == SimulatedEventType.Char && ev.Character.HasValue)
            {
                buffer.Add(ev.Character.Value);
            }
            else if (ev.EventType == SimulatedEventType.KeyDown && ev.VirtualKey == Win32Native.VK_BACK)
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }
            }
        }

        string reconstructed = new string(buffer.ToArray());
        reconstructed.Should().Be(input,
            $"Typo-injected typing must reconstruct the exact target sequence '{input}' via backspace corrections");
    }

    [Fact]
    public async Task HumanizedController_TypoInjection_ActuallyGeneratesTyposAndBackspacesWhenProbabilityHigh()
    {
        var mock = new MockInputSimulator();
        var rng = new Random(42);
        var controller = new HumanizedController(mock, rng: rng) { FastModeForTesting = true };

        string text = "qwertyuiopasdfghjklzxcvbnm";
        await controller.TypeText(text, humanize: false, injectTypos: true, typoProbability: 1.0);

        int backspaces = mock.Events.Count(e => e.EventType == SimulatedEventType.KeyDown && e.VirtualKey == Win32Native.VK_BACK);
        backspaces.Should().BeGreaterThan(20, "At 100% typo probability, nearly all alphabetic keys should inject typos and backspaces");

        int totalCharsSent = mock.Events.Count(e => e.EventType == SimulatedEventType.Char);
        totalCharsSent.Should().Be(text.Length + backspaces, "Every backspaced typo corresponds to one extra char sent");
    }
}
