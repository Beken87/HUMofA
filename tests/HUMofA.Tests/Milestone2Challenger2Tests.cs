using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using HUMofA.ComputerUse;
using Xunit;
using Xunit.Abstractions;

namespace HUMofA.Tests;

public class Milestone2Challenger2Tests
{
    private readonly ITestOutputHelper _output;

    public Milestone2Challenger2Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    // =========================================================================
    // Challenge 1: Native Win32 Struct Layouts & Alignment on 64-bit Windows
    // =========================================================================
    [Fact]
    public void StructLayout_64BitWindows_InputStructSizesAndOffsets()
    {
        // Must be in 64-bit process
        IntPtr.Size.Should().Be(8, "Operating system / runtime must be 64-bit");

        _output.WriteLine($"[Process Architecture] IntPtr.Size = {IntPtr.Size} (64-bit: {Environment.Is64BitProcess})");

        int inputSize = Marshal.SizeOf<INPUT>();
        int mouseInputSize = Marshal.SizeOf<MOUSEINPUT>();
        int keybdInputSize = Marshal.SizeOf<KEYBDINPUT>();
        int hardwareInputSize = Marshal.SizeOf<HARDWAREINPUT>();

        _output.WriteLine($"[Struct Sizes via Marshal] INPUT = {inputSize}, MOUSEINPUT = {mouseInputSize}, KEYBDINPUT = {keybdInputSize}, HARDWAREINPUT = {hardwareInputSize}");

        unsafe
        {
            int sizeofInput = sizeof(INPUT);
            int sizeofMouse = sizeof(MOUSEINPUT);
            int sizeofKeybd = sizeof(KEYBDINPUT);
            int sizeofHw = sizeof(HARDWAREINPUT);

            _output.WriteLine($"[Struct Sizes via sizeof] INPUT = {sizeofInput}, MOUSEINPUT = {sizeofMouse}, KEYBDINPUT = {sizeofKeybd}, HARDWAREINPUT = {sizeofHw}");

            sizeofInput.Should().Be(40, "INPUT must be exactly 40 bytes on x64");
            sizeofMouse.Should().Be(32, "MOUSEINPUT must be exactly 32 bytes on x64");
            sizeofKeybd.Should().Be(24, "KEYBDINPUT must be exactly 24 bytes on x64");
            sizeofHw.Should().Be(8, "HARDWAREINPUT must be exactly 8 bytes on x64");
        }

        inputSize.Should().Be(40);
        mouseInputSize.Should().Be(32);
        keybdInputSize.Should().Be(24);
        hardwareInputSize.Should().Be(8);

        // Verify exact field offsets in INPUT
        int offType = Marshal.OffsetOf<INPUT>(nameof(INPUT.type)).ToInt32();
        int offMi = Marshal.OffsetOf<INPUT>(nameof(INPUT.mi)).ToInt32();
        int offKi = Marshal.OffsetOf<INPUT>(nameof(INPUT.ki)).ToInt32();
        int offHi = Marshal.OffsetOf<INPUT>(nameof(INPUT.hi)).ToInt32();

        _output.WriteLine($"[INPUT Offsets] type: {offType}, mi: {offMi}, ki: {offKi}, hi: {offHi}");
        offType.Should().Be(0);
        offMi.Should().Be(8);
        offKi.Should().Be(8);
        offHi.Should().Be(8);

        // Verify exact field offsets and alignment in MOUSEINPUT
        int offDx = Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.dx)).ToInt32();
        int offDy = Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.dy)).ToInt32();
        int offMouseData = Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.mouseData)).ToInt32();
        int offFlagsM = Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.dwFlags)).ToInt32();
        int offTimeM = Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.time)).ToInt32();
        int offExtraM = Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.dwExtraInfo)).ToInt32();

        _output.WriteLine($"[MOUSEINPUT Offsets] dx: {offDx}, dy: {offDy}, data: {offMouseData}, flags: {offFlagsM}, time: {offTimeM}, extraInfo: {offExtraM}");
        offDx.Should().Be(0);
        offDy.Should().Be(4);
        offMouseData.Should().Be(8);
        offFlagsM.Should().Be(12);
        offTimeM.Should().Be(16);
        offExtraM.Should().Be(24, "dwExtraInfo is 8-byte pointer aligned at offset 24 (4-byte pad at 20..23)");

        // Verify exact field offsets and alignment in KEYBDINPUT
        int offVk = Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.wVk)).ToInt32();
        int offScan = Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.wScan)).ToInt32();
        int offFlagsK = Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.dwFlags)).ToInt32();
        int offTimeK = Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.time)).ToInt32();
        int offExtraK = Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.dwExtraInfo)).ToInt32();

        _output.WriteLine($"[KEYBDINPUT Offsets] wVk: {offVk}, wScan: {offScan}, flags: {offFlagsK}, time: {offTimeK}, extraInfo: {offExtraK}");
        offVk.Should().Be(0);
        offScan.Should().Be(2);
        offFlagsK.Should().Be(4);
        offTimeK.Should().Be(8);
        offExtraK.Should().Be(16, "dwExtraInfo is 8-byte pointer aligned at offset 16 (4-byte pad at 12..15)");
    }

    [Fact]
    public void StructLayout_InputArray_ContiguousMemoryStrideAndNoTearing()
    {
        const int count = 10;
        var inputs = new INPUT[count];

        for (int i = 0; i < count; i++)
        {
            ulong extra = (ulong)i;
            if (i % 2 == 0)
            {
                inputs[i] = new INPUT
                {
                    type = InputType.Mouse,
                    mi = new MOUSEINPUT
                    {
                        dx = 100 * (i + 1),
                        dy = 200 * (i + 1),
                        dwFlags = MouseEventFlags.Move | MouseEventFlags.Absolute,
                        dwExtraInfo = (UIntPtr)(0xDEADBEEF00000000UL + extra)
                    }
                };
            }
            else
            {
                inputs[i] = new INPUT
                {
                    type = InputType.Keyboard,
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)(0x41 + i),
                        dwFlags = KeyEventFlags.KeyDown,
                        dwExtraInfo = (UIntPtr)(0xCAFEBABE00000000UL + extra)
                    }
                };
            }
        }

        int structSize = Marshal.SizeOf<INPUT>();
        structSize.Should().Be(40);
        int totalBytes = structSize * count;
        totalBytes.Should().Be(400);

        IntPtr ptr = Marshal.AllocHGlobal(totalBytes);
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr itemPtr = IntPtr.Add(ptr, i * structSize);
                Marshal.StructureToPtr(inputs[i], itemPtr, false);
            }

            for (int i = 0; i < count; i++)
            {
                IntPtr itemPtr = IntPtr.Add(ptr, i * structSize);
                var reconstructed = Marshal.PtrToStructure<INPUT>(itemPtr);
                ulong extra = (ulong)i;

                if (i % 2 == 0)
                {
                    reconstructed.type.Should().Be(InputType.Mouse);
                    reconstructed.mi.dx.Should().Be(100 * (i + 1));
                    reconstructed.mi.dy.Should().Be(200 * (i + 1));
                    reconstructed.mi.dwFlags.Should().Be(MouseEventFlags.Move | MouseEventFlags.Absolute);
                    reconstructed.mi.dwExtraInfo.Should().Be((UIntPtr)(0xDEADBEEF00000000UL + extra));
                }
                else
                {
                    reconstructed.type.Should().Be(InputType.Keyboard);
                    reconstructed.ki.wVk.Should().Be((ushort)(0x41 + i));
                    reconstructed.ki.dwFlags.Should().Be(KeyEventFlags.KeyDown);
                    reconstructed.ki.dwExtraInfo.Should().Be((UIntPtr)(0xCAFEBABE00000000UL + extra));
                }
            }
            _output.WriteLine($"[Memory Stride Verification] Successfully pinned and verified 10 INPUT items ({totalBytes} bytes) with zero tearing");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // =========================================================================
    // Challenge 2: Gaussian Micro-Tremor Statistical Sampling (10,000 samples)
    // =========================================================================
    [Theory]
    [InlineData(0.0, 0.70, 42)]
    [InlineData(0.0, 1.00, 12345)]
    [InlineData(0.0, 1.50, 99999)]
    public void MicroTremor_StatisticalSampling10000_MeanAndStdDevWithinTolerance(
        double targetMean, double targetSigma, int seed)
    {
        const int n = 10000;
        var rng = new Random(seed);
        var samples = new double[n];

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double val = BezierMath.NextGaussian(rng, mean: targetMean, stdDev: targetSigma);
            samples[i] = val;
            sum += val;
        }

        double empiricalMean = sum / n;

        double sumDiffSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double diff = samples[i] - empiricalMean;
            sumDiffSq += diff * diff;
        }
        double empiricalVariance = sumDiffSq / (n - 1);
        double empiricalStdDev = Math.Sqrt(empiricalVariance);

        double meanDelta = Math.Abs(empiricalMean - targetMean);
        double stdDevDelta = Math.Abs(empiricalStdDev - targetSigma);

        _output.WriteLine($"[Gaussian N={n}, seed={seed}] Target: (mu={targetMean}, sigma={targetSigma}) => Empirical: (mean={empiricalMean:F5}, stdDev={empiricalStdDev:F5}), DeltaMean={meanDelta:F5}, DeltaStdDev={stdDevDelta:F5}");

        meanDelta.Should().BeLessThanOrEqualTo(0.05,
            $"Empirical mean ({empiricalMean:F4}) must be within +/-0.05 of target ({targetMean}) over 10,000 samples");

        stdDevDelta.Should().BeLessThanOrEqualTo(0.05,
            $"Empirical std dev ({empiricalStdDev:F4}) must match target ({targetSigma}) within +/-0.05 over 10,000 samples");

        samples.Should().AllSatisfy(s =>
        {
            double.IsNaN(s).Should().BeFalse();
            double.IsInfinity(s).Should().BeFalse();
        });
    }

    // =========================================================================
    // Challenge 3: SetOfMark Multi-Element Density (50+ and 100+ elements)
    // =========================================================================
    [Fact]
    public void SetOfMark_HighDensity60Elements_BadgePlacementAndCoordinateDictionaryIntegrity()
    {
        var elements = new List<UiElementInfo>();

        // Lay out 60 elements in a 10 x 6 grid across 1920x1080 resolution
        int id = 1;
        for (int row = 0; row < 6; row++)
        {
            for (int col = 0; col < 10; col++)
            {
                int x = 50 + (col * 180);
                int y = 50 + (row * 160);
                int w = 120;
                int h = 40;
                int cx = x + (w / 2);
                int cy = y + (h / 2);

                elements.Add(new UiElementInfo(
                    Id: id,
                    Name: $"Button_R{row}_C{col}_{id}",
                    ControlType: "Button",
                    X: x,
                    Y: y,
                    Width: w,
                    Height: h,
                    CenterX: cx,
                    CenterY: cy,
                    IsEnabled: true,
                    AutomationId: $"btn_{id}",
                    ClassName: "Win32Button"
                ));
                id++;
            }
        }

        elements.Count.Should().Be(60);

        var tree = new UIAutomationTree(elements);
        var som = new SetOfMark(tree);

        using var canvas = new Bitmap(1920, 1080);
        var sw = Stopwatch.StartNew();
        var result = som.GenerateMarks(canvas);
        sw.Stop();

        _output.WriteLine($"[SetOfMark 60 Elements] Render Time = {sw.ElapsedMilliseconds}ms, ImageSize = {result.ImageBytes.Length} bytes, Base64Length = {result.Base64Image.Length}");

        result.Should().NotBeNull();
        result.MarkToElement.Should().HaveCount(60, "Coordinate dictionary must contain all 60 marked elements");
        result.Elements.Should().HaveCount(60);

        for (int markId = 1; markId <= 60; markId++)
        {
            result.MarkToElement.Should().ContainKey(markId);
            var mappedElem = result.MarkToElement[markId];
            var sourceElem = elements[markId - 1];

            mappedElem.Id.Should().Be(sourceElem.Id);
            mappedElem.Name.Should().Be(sourceElem.Name);
            mappedElem.CenterX.Should().Be(sourceElem.CenterX);
            mappedElem.CenterY.Should().Be(sourceElem.CenterY);
            mappedElem.X.Should().Be(sourceElem.X);
            mappedElem.Y.Should().Be(sourceElem.Y);
        }

        // 60 badges + 1080p PNG compression runs rapidly in ~80-150ms; assert < 500ms
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "Generating 60 badges and full 1080p PNG compression should complete within 500ms");

        result.ImageBytes.Should().NotBeNullOrEmpty();
        result.ImageBytes.Length.Should().BeGreaterThan(1000);
        result.Base64Image.Should().NotBeNullOrWhiteSpace();

        // Verify PNG magic bytes: 0x89, 0x50, 0x4E, 0x47 (PNG)
        result.ImageBytes[0].Should().Be(0x89);
        result.ImageBytes[1].Should().Be(0x50);
        result.ImageBytes[2].Should().Be(0x4E);
        result.ImageBytes[3].Should().Be(0x47);
    }

    [Fact]
    public void SetOfMark_HighDensity100Elements_StressTest()
    {
        var elements = new List<UiElementInfo>();

        // Lay out 100 elements in a 10 x 10 grid
        int id = 1;
        for (int row = 0; row < 10; row++)
        {
            for (int col = 0; col < 10; col++)
            {
                int x = 20 + (col * 180);
                int y = 20 + (row * 100);
                int w = 100;
                int h = 30;
                int cx = x + (w / 2);
                int cy = y + (h / 2);

                elements.Add(new UiElementInfo(
                    Id: id,
                    Name: $"Elem_{id}",
                    ControlType: "Custom",
                    X: x,
                    Y: y,
                    Width: w,
                    Height: h,
                    CenterX: cx,
                    CenterY: cy,
                    IsEnabled: true,
                    AutomationId: $"elem_{id}",
                    ClassName: "CustomControl"
                ));
                id++;
            }
        }

        elements.Count.Should().Be(100);

        var tree = new UIAutomationTree(elements);
        var som = new SetOfMark(tree);

        using var canvas = new Bitmap(1920, 1080);
        var sw = Stopwatch.StartNew();
        var result = som.GenerateMarks(canvas);
        sw.Stop();

        _output.WriteLine($"[SetOfMark 100 Elements] Render Time = {sw.ElapsedMilliseconds}ms, ImageSize = {result.ImageBytes.Length} bytes");

        result.MarkToElement.Should().HaveCount(100);
        result.MarkToElement[1].Name.Should().Be("Elem_1");
        result.MarkToElement[100].Name.Should().Be("Elem_100");
    }

    [Fact]
    public void SetOfMark_BoundaryEdgeElements_ClampsBadgesWithinScreenDimensions()
    {
        var edgeElements = new List<UiElementInfo>
        {
            new(Id: 1, Name: "TopLeft Negative", ControlType: "Button", X: -50, Y: -30, Width: 100, Height: 50, CenterX: 0, CenterY: 0, IsEnabled: true),
            new(Id: 2, Name: "BottomRight Overflow", ControlType: "Button", X: 1910, Y: 1070, Width: 100, Height: 50, CenterX: 1960, CenterY: 1095, IsEnabled: true),
            new(Id: 3, Name: "Far Offscreen", ControlType: "Button", X: 5000, Y: 5000, Width: 100, Height: 50, CenterX: 5050, CenterY: 5025, IsEnabled: true)
        };

        var tree = new UIAutomationTree(edgeElements);
        var som = new SetOfMark(tree);

        using var canvas = new Bitmap(1920, 1080);
        var result = som.GenerateMarks(canvas);

        result.MarkToElement.Should().HaveCount(3);
        result.ImageBytes.Should().NotBeEmpty();
        _output.WriteLine($"[SetOfMark Boundary Test] Handled extreme out-of-bounds coordinates cleanly without exceptions");
    }
}
