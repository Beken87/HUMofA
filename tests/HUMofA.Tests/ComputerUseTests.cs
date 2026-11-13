using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HUMofA.ComputerUse;
using Xunit;

namespace HUMofA.Tests;

public class ComputerUseTests
{
    // =========================================================================
    // 1. Win32 Struct Layout & Memory Alignment
    // =========================================================================
    [Fact]
    public void Win32Native_InputStructSize_IsExactly40BytesOn64Bit()
    {
        // 64-bit Windows requirement: sizeof(INPUT) == 40 bytes
        int inputSize = Marshal.SizeOf<INPUT>();
        inputSize.Should().Be(40, "64-bit Windows SendInput requires strictly 40 bytes for INPUT struct");

        unsafe
        {
            sizeof(INPUT).Should().Be(40);
        }

        int mouseInputSize = Marshal.SizeOf<MOUSEINPUT>();
        mouseInputSize.Should().Be(32, "MOUSEINPUT must be 32 bytes on 64-bit");

        int keybdInputSize = Marshal.SizeOf<KEYBDINPUT>();
        keybdInputSize.Should().Be(24, "KEYBDINPUT must be 24 bytes on 64-bit");
    }

    [Fact]
    public void Win32Native_InputStruct_HasCorrectFieldOffsets()
    {
        var input = new INPUT { type = InputType.Keyboard };
        input.ki.wVk = 0x41; // 'A'

        input.type.Should().Be(InputType.Keyboard);
        input.ki.wVk.Should().Be(0x41);

        // Verify union sharing offset 8
        input.type = InputType.Mouse;
        input.mi.dx = 12345;
        input.mi.dy = 67890;
        input.mi.dx.Should().Be(12345);
        input.mi.dy.Should().Be(67890);
    }

    // =========================================================================
    // 2. Bezier Mathematics & Trajectory
    // =========================================================================
    [Fact]
    public void BezierMath_EndpointConvergence_P0andP3()
    {
        var p0 = new Point2D(100, 200);
        var p1 = new Point2D(150, 250);
        var p2 = new Point2D(300, 450);
        var p3 = new Point2D(500, 600);

        Point2D atStart = BezierMath.EvaluateCubicBezier(p0, p1, p2, p3, 0.0);
        Point2D atEnd = BezierMath.EvaluateCubicBezier(p0, p1, p2, p3, 1.0);

        atStart.X.Should().BeApproximately(p0.X, 0.0001);
        atStart.Y.Should().BeApproximately(p0.Y, 0.0001);

        atEnd.X.Should().BeApproximately(p3.X, 0.0001);
        atEnd.Y.Should().BeApproximately(p3.Y, 0.0001);
    }

    [Fact]
    public void BezierMath_MinimumJerk_IsMonotonicAndBounded()
    {
        BezierMath.MinimumJerk(0.0).Should().Be(0.0);
        BezierMath.MinimumJerk(1.0).Should().Be(1.0);

        double previous = 0.0;
        for (int i = 1; i <= 100; i++)
        {
            double tau = i / 100.0;
            double current = BezierMath.MinimumJerk(tau);

            current.Should().BeGreaterOrEqualTo(previous, "minimum jerk easing function must be strictly monotonic");
            current.Should().BeInRange(0.0, 1.0);

            previous = current;
        }
    }

    [Fact]
    public void BezierMath_GenerateTrajectory_StartsAtP0AndEndsAtP3()
    {
        var p0 = new Point2D(50, 100);
        var p3 = new Point2D(800, 600);

        var trajectory = BezierMath.GenerateTrajectory(p0, p3, targetWidth: 40.0, addMicroTremor: true);

        trajectory.Should().NotBeEmpty();
        trajectory.Count.Should().BeGreaterThan(50);

        // First point should be close to p0
        trajectory[0].X.Should().BeApproximately(p0.X, 3.0);
        trajectory[0].Y.Should().BeApproximately(p0.Y, 3.0);

        // Final point must converge exactly to target P3
        trajectory[^1].X.Should().Be(p3.X);
        trajectory[^1].Y.Should().Be(p3.Y);
    }

    // =========================================================================
    // 3. Fitts's Law Scaling & Properties
    // =========================================================================
    [Theory]
    [InlineData(100, 1000, 40)]
    [InlineData(50, 500, 20)]
    [InlineData(200, 1500, 50)]
    public void BezierMath_FittsLaw_IncreasesWithDistance(double shortDist, double longDist, double targetWidth)
    {
        double mtShort = BezierMath.CalculateFittsDuration(shortDist, targetWidth);
        double mtLong = BezierMath.CalculateFittsDuration(longDist, targetWidth);

        mtLong.Should().BeGreaterThan(mtShort, "larger distance requires longer movement time according to Fitts's Law");
        mtShort.Should().BeInRange(BezierMath.MinMovementDuration, BezierMath.MaxMovementDuration);
        mtLong.Should().BeInRange(BezierMath.MinMovementDuration, BezierMath.MaxMovementDuration);
    }

    [Fact]
    public void BezierMath_FittsLaw_DecreasesWithTargetWidth()
    {
        double distance = 500.0;
        double mtNarrow = BezierMath.CalculateFittsDuration(distance, targetWidth: 10.0);
        double mtWide = BezierMath.CalculateFittsDuration(distance, targetWidth: 100.0);

        mtNarrow.Should().BeGreaterThan(mtWide, "smaller targets have higher index of difficulty and take longer");
    }

    // =========================================================================
    // 4. Gaussian Micro-Tremor Statistical Properties
    // =========================================================================
    [Fact]
    public void BezierMath_MicroTremor_StatisticalProperties_MeanNearZeroAndBoundedVariance()
    {
        var rng = new Random(42);
        int sampleCount = 5000;
        double targetSigma = 0.7;

        double sum = 0.0;
        double sumSq = 0.0;
        double maxAbs = 0.0;

        for (int i = 0; i < sampleCount; i++)
        {
            double sample = BezierMath.NextGaussian(rng, mean: 0.0, stdDev: targetSigma);
            sum += sample;
            sumSq += sample * sample;
            maxAbs = Math.Max(maxAbs, Math.Abs(sample));
        }

        double mean = sum / sampleCount;
        double variance = (sumSq / sampleCount) - (mean * mean);
        double stdDev = Math.Sqrt(variance);

        mean.Should().BeInRange(-0.06, 0.06, "micro-tremor Gaussian mean should center near 0");
        stdDev.Should().BeInRange(0.64, 0.76, "micro-tremor standard deviation should match parameter");
        maxAbs.Should().BeLessThan(targetSigma * 5.0, "extreme outliers beyond 5-sigma should be vanishingly rare");
    }

    // =========================================================================
    // 5. Panic Kill-Switch Latency & Trigger Mechanics
    // =========================================================================
    [Fact]
    public void PanicKillSwitch_Sub10MsCutoffLatency_Guarantee()
    {
        var mock = new MockInputSimulator();
        using var killSwitch = new PanicKillSwitch(mock, installHooks: false);

        // Run benchmark across 10 iterations to verify consistent sub-10ms performance
        for (int i = 0; i < 10; i++)
        {
            killSwitch.Reset();
            killSwitch.IsPanicTriggered.Should().BeFalse();

            var sw = Stopwatch.StartNew();
            double reportedLatencyMs = killSwitch.TriggerPanic($"Benchmark test {i}");
            sw.Stop();

            double actualLatencyMs = sw.Elapsed.TotalMilliseconds;

            killSwitch.IsPanicTriggered.Should().BeTrue();
            killSwitch.CancellationToken.IsCancellationRequested.Should().BeTrue();

            // Strictly assert sub-10ms cutoff guarantee
            reportedLatencyMs.Should().BeLessThanOrEqualTo(10.0, "Panic kill switch MUST terminate active input in <= 10ms");
            actualLatencyMs.Should().BeLessThanOrEqualTo(10.0, "Total elapsed benchmark time must also be <= 10ms");
        }
    }

    [Fact]
    public void PanicKillSwitch_PhysicalMouseVelocityThreshold_TriggersPanic()
    {
        var mock = new MockInputSimulator();
        using var killSwitch = new PanicKillSwitch(mock, installHooks: false);

        // Initial reference point
        killSwitch.ProcessMouseEvent(x: 100, y: 100, flags: 0, timestampMs: 1000);
        killSwitch.IsPanicTriggered.Should().BeFalse();

        // Slow movement: 5px in 100ms = 50 px/s (< 200 px/s threshold)
        bool triggeredSlow = killSwitch.ProcessMouseEvent(x: 105, y: 100, flags: 0, timestampMs: 1100);
        triggeredSlow.Should().BeFalse();
        killSwitch.IsPanicTriggered.Should().BeFalse();

        // Injected movement (LLMHF_INJECTED = 0x01) fast movement: 100px in 50ms = 2000 px/s
        bool triggeredInjected = killSwitch.ProcessMouseEvent(
            x: 205, y: 100, flags: Win32Native.LLMHF_INJECTED, timestampMs: 1150);
        triggeredInjected.Should().BeFalse("Injected automated movement must NOT trip physical panic kill switch");
        killSwitch.IsPanicTriggered.Should().BeFalse();

        // Physical fast movement (no injected flag): 100px in 50ms = 2000 px/s (> 200 px/s)
        bool triggeredFast = killSwitch.ProcessMouseEvent(x: 305, y: 100, flags: 0, timestampMs: 1200);
        triggeredFast.Should().BeTrue("Physical human mouse movement exceeding 200px/s MUST trip panic kill switch");
        killSwitch.IsPanicTriggered.Should().BeTrue();
    }

    [Fact]
    public void PanicKillSwitch_Hotkey_CtrlAltSpace_TriggersPanic()
    {
        var mock = new MockInputSimulator();
        using var killSwitch = new PanicKillSwitch(mock, installHooks: false);

        // Press Ctrl
        killSwitch.ProcessKeyEvent(Win32Native.VK_CONTROL, flags: 0);
        killSwitch.IsPanicTriggered.Should().BeFalse();

        // Press Alt
        killSwitch.ProcessKeyEvent(Win32Native.VK_MENU, flags: 0);
        killSwitch.IsPanicTriggered.Should().BeFalse();

        // Press Space while Ctrl and Alt held -> Panic!
        bool triggered = killSwitch.ProcessKeyEvent(Win32Native.VK_SPACE, flags: 0);
        triggered.Should().BeTrue();
        killSwitch.IsPanicTriggered.Should().BeTrue();
        killSwitch.PanicReason.Should().Contain("Ctrl+Alt+Space");
    }

    [Fact]
    public async Task PanicKillSwitch_AbortsActiveMovement_Immediately()
    {
        var mock = new MockInputSimulator(0, 0);
        using var killSwitch = new PanicKillSwitch(mock, installHooks: false);
        var controller = new HumanizedController(mock, killSwitch) { FastModeForTesting = true };

        // Pre-trigger panic
        killSwitch.TriggerPanic("Simulated emergency cut-off");

        // Attempting to move should throw OperationCanceledException immediately
        Func<Task> action = async () => await controller.MoveTo(500, 500);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    // =========================================================================
    // 6. 14 Computer Actions Execution via MockInputSimulator
    // =========================================================================
    [Fact]
    public async Task HumanizedController_Click_RecordsMouseDownAndUpAtCoordinates()
    {
        var mock = new MockInputSimulator(10, 10);
        var controller = new HumanizedController(mock) { FastModeForTesting = true };

        await controller.Click(300, 400, MouseButton.Left, clicks: 1);

        mock.Events.Should().Contain(e => e.EventType == SimulatedEventType.MouseDown && e.Button == MouseButton.Left);
        mock.Events.Should().Contain(e => e.EventType == SimulatedEventType.MouseUp && e.Button == MouseButton.Left);

        var (cx, cy) = mock.GetCursorPosition();
        cx.Should().Be(300);
        cy.Should().Be(400);
    }

    [Fact]
    public async Task HumanizedController_DoubleClick_RecordsTwoClicks()
    {
        var mock = new MockInputSimulator();
        var controller = new HumanizedController(mock) { FastModeForTesting = true };

        await controller.DoubleClick(150, 250);

        int downs = mock.Events.Count(e => e.EventType == SimulatedEventType.MouseDown && e.Button == MouseButton.Left);
        int ups = mock.Events.Count(e => e.EventType == SimulatedEventType.MouseUp && e.Button == MouseButton.Left);

        downs.Should().Be(2);
        ups.Should().Be(2);
    }

    [Fact]
    public async Task HumanizedController_RightClick_RecordsRightButton()
    {
        var mock = new MockInputSimulator();
        var controller = new HumanizedController(mock) { FastModeForTesting = true };

        await controller.Click(100, 100, MouseButton.Right, clicks: 1);

        mock.Events.Should().Contain(e => e.EventType == SimulatedEventType.MouseDown && e.Button == MouseButton.Right);
        mock.Events.Should().Contain(e => e.EventType == SimulatedEventType.MouseUp && e.Button == MouseButton.Right);
    }

    [Fact]
    public async Task HumanizedController_DragAndDrop_ExecutesCompleteDragSequence()
    {
        var mock = new MockInputSimulator(0, 0);
        var controller = new HumanizedController(mock) { FastModeForTesting = true };

        await controller.DragAndDrop(startX: 100, startY: 100, endX: 500, endY: 500);

        var events = mock.Events;
        int downIdx = events.ToList().FindIndex(e => e.EventType == SimulatedEventType.MouseDown);
        int upIdx = events.ToList().FindIndex(e => e.EventType == SimulatedEventType.MouseUp);

        downIdx.Should().BeGreaterThan(-1);
        upIdx.Should().BeGreaterThan(downIdx);

        // Verify cursor reached destination
        var (finalX, finalY) = mock.GetCursorPosition();
        finalX.Should().Be(500);
        finalY.Should().Be(500);
    }

    [Fact]
    public void HumanizedController_Scroll_RecordsMouseWheelEvents()
    {
        var mock = new MockInputSimulator();
        var controller = new HumanizedController(mock);

        controller.Scroll(deltaX: 120, deltaY: -240);

        mock.Events.Should().Contain(e => e.EventType == SimulatedEventType.MouseWheel && e.DeltaX == 120 && e.DeltaY == -240);
    }

    [Fact]
    public async Task HumanizedController_PressKey_Combo_ExecutesInOrder()
    {
        var mock = new MockInputSimulator();
        var controller = new HumanizedController(mock) { FastModeForTesting = true };

        await controller.PressKey("Ctrl+C");

        var events = mock.Events;
        events.Should().HaveCount(4);

        events[0].EventType.Should().Be(SimulatedEventType.KeyDown);
        events[0].VirtualKey.Should().Be(Win32Native.VK_CONTROL);

        events[1].EventType.Should().Be(SimulatedEventType.KeyDown);
        events[1].VirtualKey.Should().Be((ushort)'C');

        events[2].EventType.Should().Be(SimulatedEventType.KeyUp);
        events[2].VirtualKey.Should().Be((ushort)'C');

        events[3].EventType.Should().Be(SimulatedEventType.KeyUp);
        events[3].VirtualKey.Should().Be(Win32Native.VK_CONTROL);
    }

    [Fact]
    public void HumanizedController_Hotkey_DispatchesMultipleKeys()
    {
        var mock = new MockInputSimulator();
        var controller = new HumanizedController(mock);

        controller.Hotkey(Win32Native.VK_CONTROL, Win32Native.VK_SHIFT, Win32Native.VK_ESCAPE);

        var events = mock.Events;
        events.Should().HaveCount(6);

        // Key downs
        events[0].VirtualKey.Should().Be(Win32Native.VK_CONTROL);
        events[1].VirtualKey.Should().Be(Win32Native.VK_SHIFT);
        events[2].VirtualKey.Should().Be(Win32Native.VK_ESCAPE);

        // Key ups (reverse order)
        events[3].VirtualKey.Should().Be(Win32Native.VK_ESCAPE);
        events[4].VirtualKey.Should().Be(Win32Native.VK_SHIFT);
        events[5].VirtualKey.Should().Be(Win32Native.VK_CONTROL);
    }

    // =========================================================================
    // 7. Human Typing Cadence & Typo Injection
    // =========================================================================
    [Fact]
    public async Task HumanizedController_TypeText_PreservesCharacterSequence()
    {
        var mock = new MockInputSimulator();
        var controller = new HumanizedController(mock) { FastModeForTesting = true };

        string text = "HUMofA Platform 2026!";
        await controller.TypeText(text, humanize: true, injectTypos: false);

        var typedChars = mock.Events
            .Where(e => e.EventType == SimulatedEventType.Char)
            .Select(e => e.Character!.Value)
            .ToArray();

        new string(typedChars).Should().Be(text);
    }

    [Fact]
    public async Task HumanizedController_TypeText_TypoInjectionAndCorrection_ReconstructsCleanString()
    {
        var mock = new MockInputSimulator();
        var rng = new Random(12345);
        var controller = new HumanizedController(mock, rng: rng) { FastModeForTesting = true };

        string text = "hello";
        // Force typo injection on every character that has neighbors
        await controller.TypeText(text, humanize: false, injectTypos: true, typoProbability: 1.0);

        // Verify that Backspace keys were pressed for typo corrections
        int backspaceDownCount = mock.Events.Count(e => e.EventType == SimulatedEventType.KeyDown && e.VirtualKey == Win32Native.VK_BACK);
        backspaceDownCount.Should().BeGreaterThan(0, "Typos must trigger backspace corrections");

        // Reconstruct the text as a terminal would process it:
        var buffer = new List<char>();
        foreach (var ev in mock.Events)
        {
            if (ev.EventType == SimulatedEventType.Char)
            {
                buffer.Add(ev.Character!.Value);
            }
            else if (ev.EventType == SimulatedEventType.KeyDown && ev.VirtualKey == Win32Native.VK_BACK)
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }
            }
        }

        new string(buffer.ToArray()).Should().Be(text, "after backspace correction, the final text must equal original string");
    }

    // =========================================================================
    // 8. Set-of-Mark (SOM) Perception & UI Automation
    // =========================================================================
    [Fact]
    public void SetOfMark_GenerateMarks_AssignsNumberedBadgesAndCoordinates()
    {
        var mockElements = new List<UiElementInfo>
        {
            new(Id: 1, Name: "Submit Button", ControlType: "Button", X: 100, Y: 200, Width: 80, Height: 30, CenterX: 140, CenterY: 215, IsEnabled: true),
            new(Id: 2, Name: "Search Box", ControlType: "Edit", X: 300, Y: 50, Width: 200, Height: 35, CenterX: 400, CenterY: 67, IsEnabled: true),
            new(Id: 3, Name: "Dashboard Tab", ControlType: "TabItem", X: 20, Y: 10, Width: 100, Height: 25, CenterX: 70, CenterY: 22, IsEnabled: true)
        };

        var uiProvider = new UIAutomationTree(mockElements);
        var som = new SetOfMark(uiProvider);

        using var dummyImage = new Bitmap(1920, 1080);
        var result = som.GenerateMarks(dummyImage);

        result.Should().NotBeNull();
        result.ImageBytes.Should().NotBeEmpty();
        result.Base64Image.Should().NotBeNullOrEmpty();
        result.MarkToElement.Should().HaveCount(3);

        result.MarkToElement[1].Name.Should().Be("Submit Button");
        result.MarkToElement[1].CenterX.Should().Be(140);
        result.MarkToElement[1].CenterY.Should().Be(215);

        result.MarkToElement[2].Name.Should().Be("Search Box");
        result.MarkToElement[3].Name.Should().Be("Dashboard Tab");
    }

    [Fact]
    public void UIAutomationTree_FindElementByIdAndName_ReturnsCorrectElements()
    {
        var mockElements = new List<UiElementInfo>
        {
            new(Id: 10, Name: "Settings Window", ControlType: "Window", X: 0, Y: 0, Width: 500, Height: 400, CenterX: 250, CenterY: 200, IsEnabled: true)
        };

        var tree = new UIAutomationTree(mockElements);

        var byId = tree.FindElementById(10);
        byId.Should().NotBeNull();
        byId!.Name.Should().Be("Settings Window");

        var byName = tree.FindElementByName("settings");
        byName.Should().NotBeNull();
        byName!.Id.Should().Be(10);

        var missing = tree.FindElementById(999);
        missing.Should().BeNull();
    }

    [Fact]
    public async Task HumanizedController_ClickByMarkId_MovesAndClicksElementCenter()
    {
        var mockElements = new List<UiElementInfo>
        {
            new(Id: 5, Name: "Save", ControlType: "Button", X: 200, Y: 300, Width: 50, Height: 20, CenterX: 225, CenterY: 310, IsEnabled: true)
        };

        var mock = new MockInputSimulator(0, 0);
        var tree = new UIAutomationTree(mockElements);
        var controller = new HumanizedController(mock, uiAutomation: tree) { FastModeForTesting = true };

        await controller.Click(markId: 5);

        var (cx, cy) = mock.GetCursorPosition();
        cx.Should().Be(225);
        cy.Should().Be(310);

        mock.Events.Should().Contain(e => e.EventType == SimulatedEventType.MouseDown && e.Button == MouseButton.Left);
        mock.Events.Should().Contain(e => e.EventType == SimulatedEventType.MouseUp && e.Button == MouseButton.Left);
    }
}
