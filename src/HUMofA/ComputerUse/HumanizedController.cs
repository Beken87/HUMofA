using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HUMofA.ComputerUse;

public class HumanizedController
{
    private readonly IInputSimulator _inputSimulator;
    private readonly PanicKillSwitch? _panicKillSwitch;
    private readonly IUIAutomationProvider _uiAutomation;
    private readonly SetOfMark _setOfMark;
    private readonly Random _rng;

    public bool FastModeForTesting { get; set; } = false;

    private static readonly Dictionary<char, char[]> QwertyNeighbors = new()
    {
        ['q'] = ['w', 'a', 's'],
        ['w'] = ['q', 'e', 'a', 's', 'd'],
        ['e'] = ['w', 'r', 's', 'd', 'f'],
        ['r'] = ['e', 't', 'd', 'f', 'g'],
        ['t'] = ['r', 'y', 'f', 'g', 'h'],
        ['y'] = ['t', 'u', 'g', 'h', 'j'],
        ['u'] = ['y', 'i', 'h', 'j', 'k'],
        ['i'] = ['u', 'o', 'j', 'k', 'l'],
        ['o'] = ['i', 'p', 'k', 'l'],
        ['p'] = ['o', 'l'],
        ['a'] = ['q', 'w', 's', 'z'],
        ['s'] = ['w', 'e', 'a', 'd', 'z', 'x'],
        ['d'] = ['e', 'r', 's', 'f', 'x', 'c'],
        ['f'] = ['r', 't', 'd', 'g', 'c', 'v'],
        ['g'] = ['t', 'y', 'f', 'h', 'v', 'b'],
        ['h'] = ['y', 'u', 'g', 'j', 'b', 'n'],
        ['j'] = ['u', 'i', 'h', 'k', 'n', 'm'],
        ['k'] = ['i', 'o', 'j', 'l', 'm'],
        ['l'] = ['o', 'p', 'k'],
        ['z'] = ['a', 's', 'x'],
        ['x'] = ['z', 's', 'd', 'c'],
        ['c'] = ['x', 'd', 'f', 'v'],
        ['v'] = ['c', 'f', 'g', 'b'],
        ['b'] = ['v', 'g', 'h', 'n'],
        ['n'] = ['b', 'h', 'j', 'm'],
        ['m'] = ['n', 'j', 'k']
    };

    public HumanizedController(
        IInputSimulator? inputSimulator = null,
        PanicKillSwitch? panicKillSwitch = null,
        IUIAutomationProvider? uiAutomation = null,
        Random? rng = null)
    {
        _inputSimulator = inputSimulator ?? new Win32InputSimulator();
        _panicKillSwitch = panicKillSwitch;
        _uiAutomation = uiAutomation ?? new UIAutomationTree();
        _setOfMark = new SetOfMark(_uiAutomation);
        _rng = rng ?? Random.Shared;
    }

    public IInputSimulator InputSimulator => _inputSimulator;
    public PanicKillSwitch? PanicKillSwitch => _panicKillSwitch;
    public IUIAutomationProvider UIAutomation => _uiAutomation;

    // Action 1: Click
    public async Task Click(int x, int y, MouseButton button = MouseButton.Left, int clicks = 1, CancellationToken ct = default)
    {
        await MoveTo(x, y, targetWidth: 30.0, ct: ct);

        for (int i = 0; i < clicks; i++)
        {
            CheckPanicOrCancel(ct);
            _inputSimulator.SendMouseButton(button, isDown: true);

            int clickHoldMs = FastModeForTesting ? 0 : _rng.Next(40, 80);
            if (clickHoldMs > 0) await Task.Delay(clickHoldMs, ct);

            CheckPanicOrCancel(ct);
            _inputSimulator.SendMouseButton(button, isDown: false);

            if (i < clicks - 1)
            {
                int multiClickPauseMs = FastModeForTesting ? 0 : _rng.Next(60, 120);
                if (multiClickPauseMs > 0) await Task.Delay(multiClickPauseMs, ct);
            }
        }
    }

    public async Task Click(int markId, MouseButton button = MouseButton.Left, int clicks = 1, CancellationToken ct = default)
    {
        var elem = _uiAutomation.FindElementById(markId)
            ?? throw new ArgumentException($"Element with mark ID {markId} not found.");
        await Click(elem.CenterX, elem.CenterY, button, clicks, ct);
    }

    // Action 2: MoveTo (with Bezier trajectory, Fitts's Law, and micro-tremor)
    public async Task MoveTo(int targetX, int targetY, double targetWidth = 40.0, CancellationToken ct = default)
    {
        var (currX, currY) = _inputSimulator.GetCursorPosition();
        var p0 = new Point2D(currX, currY);
        var p3 = new Point2D(targetX, targetY);

        if (p0 == p3)
        {
            return;
        }

        var trajectory = BezierMath.GenerateTrajectory(
            p0,
            p3,
            targetWidth: targetWidth,
            addMicroTremor: true,
            rng: _rng
        );

        int stepDelayMs = FastModeForTesting ? 0 : 1;

        foreach (var pt in trajectory)
        {
            CheckPanicOrCancel(ct);
            _inputSimulator.SendMouseMove((int)Math.Round(pt.X), (int)Math.Round(pt.Y));

            if (stepDelayMs > 0)
            {
                await Task.Delay(stepDelayMs, ct);
            }
        }

        // Final snap to target
        _inputSimulator.SendMouseMove(targetX, targetY);
    }

    // Action 3: MouseDown
    public void MouseDown(MouseButton button = MouseButton.Left)
    {
        CheckPanicOrCancel(CancellationToken.None);
        _inputSimulator.SendMouseButton(button, isDown: true);
    }

    // Action 4: MouseUp
    public void MouseUp(MouseButton button = MouseButton.Left)
    {
        CheckPanicOrCancel(CancellationToken.None);
        _inputSimulator.SendMouseButton(button, isDown: false);
    }

    // Action 5: DragAndDrop
    public async Task DragAndDrop(int startX, int startY, int endX, int endY, CancellationToken ct = default)
    {
        await MoveTo(startX, startY, targetWidth: 30.0, ct: ct);
        MouseDown(MouseButton.Left);

        int dragPause = FastModeForTesting ? 0 : _rng.Next(80, 150);
        if (dragPause > 0) await Task.Delay(dragPause, ct);

        await MoveTo(endX, endY, targetWidth: 30.0, ct: ct);

        int dropPause = FastModeForTesting ? 0 : _rng.Next(60, 120);
        if (dropPause > 0) await Task.Delay(dropPause, ct);

        MouseUp(MouseButton.Left);
    }

    // Action 6: TypeText (with log-normal human cadence, bigrams, and typo correction)
    public async Task TypeText(
        string text,
        bool humanize = true,
        bool injectTypos = false,
        double typoProbability = 0.018,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (int i = 0; i < text.Length; i++)
        {
            CheckPanicOrCancel(ct);
            char c = text[i];
            char lowerC = char.ToLowerInvariant(c);

            // Typo injection simulation
            if (injectTypos && QwertyNeighbors.TryGetValue(lowerC, out var neighbors) && neighbors.Length > 0)
            {
                if (_rng.NextDouble() < typoProbability)
                {
                    char typoChar = neighbors[_rng.Next(neighbors.Length)];
                    if (char.IsUpper(c)) typoChar = char.ToUpperInvariant(typoChar);

                    // Type error
                    _inputSimulator.SendChar(typoChar);

                    // Cognitive reaction pause (180 - 360ms)
                    int detectPause = FastModeForTesting ? 0 : _rng.Next(180, 360);
                    if (detectPause > 0) await Task.Delay(detectPause, ct);

                    CheckPanicOrCancel(ct);
                    // Press Backspace to correct
                    _inputSimulator.SendKey(Win32Native.VK_BACK, isDown: true);
                    _inputSimulator.SendKey(Win32Native.VK_BACK, isDown: false);

                    // Resume pause (80 - 160ms)
                    int resumePause = FastModeForTesting ? 0 : _rng.Next(80, 160);
                    if (resumePause > 0) await Task.Delay(resumePause, ct);
                }
            }

            CheckPanicOrCancel(ct);
            _inputSimulator.SendChar(c);

            if (humanize && !FastModeForTesting)
            {
                int delay = CalculateKeyDelay(text, i);
                if (delay > 0)
                {
                    await Task.Delay(delay, ct);
                }
            }
        }
    }

    // Action 7: PressKey (combo like "Ctrl+C" or virtual key)
    public async Task PressKey(string combo, CancellationToken ct = default)
    {
        CheckPanicOrCancel(ct);
        var keys = ParseKeyCombo(combo);

        // Key down in order
        foreach (var key in keys)
        {
            _inputSimulator.SendKey(key, isDown: true);
        }

        int holdMs = FastModeForTesting ? 0 : _rng.Next(30, 60);
        if (holdMs > 0) await Task.Delay(holdMs, ct);

        // Key up in reverse order
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            _inputSimulator.SendKey(keys[i], isDown: false);
        }
    }

    public void PressKey(ushort virtualKey)
    {
        CheckPanicOrCancel(CancellationToken.None);
        _inputSimulator.SendKey(virtualKey, isDown: true);
        _inputSimulator.SendKey(virtualKey, isDown: false);
    }

    // Action 8: KeyDown
    public void KeyDown(ushort virtualKey)
    {
        CheckPanicOrCancel(CancellationToken.None);
        _inputSimulator.SendKey(virtualKey, isDown: true);
    }

    // Action 9: KeyUp
    public void KeyUp(ushort virtualKey)
    {
        CheckPanicOrCancel(CancellationToken.None);
        _inputSimulator.SendKey(virtualKey, isDown: false);
    }

    // Action 10: Scroll
    public void Scroll(int deltaX, int deltaY)
    {
        CheckPanicOrCancel(CancellationToken.None);
        _inputSimulator.SendMouseWheel(deltaX, deltaY);
    }

    // Action 11: SOM (Set of Mark)
    public SetOfMarkResult Som()
    {
        return _setOfMark.GenerateMarks();
    }

    // Action 12: FocusWindow
    public bool FocusWindow(string titleOrHwnd)
    {
        if (long.TryParse(titleOrHwnd, out long hwndVal))
        {
            IntPtr hWnd = new IntPtr(hwndVal);
            Win32Native.ShowWindow(hWnd, Win32Native.SW_RESTORE);
            return Win32Native.SetForegroundWindow(hWnd);
        }

        var elem = _uiAutomation.FindElementByName(titleOrHwnd);
        if (elem != null && elem.AutomationId.StartsWith("hwnd_") &&
            long.TryParse(elem.AutomationId[5..], out long foundHwnd))
        {
            IntPtr hWnd = new IntPtr(foundHwnd);
            Win32Native.ShowWindow(hWnd, Win32Native.SW_RESTORE);
            return Win32Native.SetForegroundWindow(hWnd);
        }

        return false;
    }

    // Action 13: ListWindows
    public IReadOnlyList<UiElementInfo> ListWindows()
    {
        return _uiAutomation.GetInteractiveElements();
    }

    // Action 14: DoubleClick
    public async Task DoubleClick(int x, int y, CancellationToken ct = default)
    {
        await Click(x, y, MouseButton.Left, clicks: 2, ct: ct);
    }

    // Extra: Hover
    public async Task Hover(int x, int y, int hoverMs = 500, CancellationToken ct = default)
    {
        await MoveTo(x, y, targetWidth: 40.0, ct: ct);
        int waitMs = FastModeForTesting ? 0 : hoverMs;
        if (waitMs > 0)
        {
            await Task.Delay(waitMs, ct);
        }
    }

    // Extra: Hotkey
    public void Hotkey(params ushort[] keys)
    {
        CheckPanicOrCancel(CancellationToken.None);
        foreach (var key in keys)
        {
            _inputSimulator.SendKey(key, isDown: true);
        }
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            _inputSimulator.SendKey(keys[i], isDown: false);
        }
    }

    private void CheckPanicOrCancel(CancellationToken ct)
    {
        if (_panicKillSwitch?.IsPanicTriggered == true)
        {
            throw new OperationCanceledException($"Panic kill switch triggered: {_panicKillSwitch.PanicReason}");
        }
        ct.ThrowIfCancellationRequested();
    }

    private int CalculateKeyDelay(string text, int currentIndex)
    {
        // Log-normal distribution: ln(dt) ~ N(mu=4.40, sigma=0.22) -> mean ~ 81ms
        double z = BezierMath.NextGaussian(_rng, mean: 0.0, stdDev: 1.0);
        double logVal = 4.40 + (0.22 * z);
        double delay = Math.Exp(logVal);

        char c = text[currentIndex];

        // Bigram adjustment
        if (currentIndex > 0)
        {
            char prev = text[currentIndex - 1];
            string bigram = $"{char.ToLowerInvariant(prev)}{char.ToLowerInvariant(c)}";

            if (bigram is "th" or "er" or "in" or "an" or "on" or "at")
            {
                delay *= 0.65; // Fast digraph
            }
            else if (prev == c)
            {
                delay *= 1.30; // Double letter repeat
            }
        }

        if (c is ' ' or '.' or ',' or '!' or '?' or ';' or ':')
        {
            delay *= 1.70; // Space or punctuation pause
        }
        else if (char.IsUpper(c))
        {
            delay += 60.0; // Shift modifier delay
        }

        return (int)Math.Clamp(Math.Round(delay), 35.0, 260.0);
    }

    private static List<ushort> ParseKeyCombo(string combo)
    {
        var result = new List<ushort>();
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            ushort vk = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Win32Native.VK_CONTROL,
                "alt" or "menu" => Win32Native.VK_MENU,
                "shift" => Win32Native.VK_SHIFT,
                "enter" or "return" => Win32Native.VK_RETURN,
                "space" => Win32Native.VK_SPACE,
                "tab" => Win32Native.VK_TAB,
                "esc" or "escape" => Win32Native.VK_ESCAPE,
                "back" or "backspace" => Win32Native.VK_BACK,
                "del" or "delete" => Win32Native.VK_DELETE,
                "up" => Win32Native.VK_UP,
                "down" => Win32Native.VK_DOWN,
                "left" => Win32Native.VK_LEFT,
                "right" => Win32Native.VK_RIGHT,
                _ when part.Length == 1 => (ushort)char.ToUpperInvariant(part[0]),
                _ => throw new ArgumentException($"Unknown key in combination: '{part}'")
            };

            result.Add(vk);
        }

        return result;
    }
}
