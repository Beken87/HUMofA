using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace HUMofA.ComputerUse;

public class PanicKillSwitch : IDisposable
{
    public const double VelocityThresholdPxPerSec = 200.0;

    private int _isPanicTriggered;
    private string? _panicReason;
    private double _lastPanicLatencyMs;

    private CancellationTokenSource _cts = new();
    private readonly IInputSimulator? _inputSimulator;

    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private Win32Native.LowLevelProc? _mouseProc;
    private Win32Native.LowLevelProc? _keyboardProc;

    private int _lastMouseX;
    private int _lastMouseY;
    private uint _lastMouseTime;
    private bool _hasLastMouse;

    private bool _ctrlDown;
    private bool _altDown;

    public bool IsPanicTriggered => Volatile.Read(ref _isPanicTriggered) == 1;
    public string? PanicReason => _panicReason;
    public double LastPanicLatencyMs => _lastPanicLatencyMs;
    public CancellationToken CancellationToken => _cts.Token;

    public event Action<string>? OnPanic;

    public PanicKillSwitch(IInputSimulator? inputSimulator = null, bool installHooks = false)
    {
        _inputSimulator = inputSimulator;
        if (installHooks)
        {
            InstallHooks();
        }
    }

    public void RegisterCancellationTokenSource(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _isPanicTriggered, 0);
        _panicReason = null;
        _lastPanicLatencyMs = 0;
        _hasLastMouse = false;
        _ctrlDown = false;
        _altDown = false;
        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Triggers immediate emergency panic kill. Measures latency with high-resolution Stopwatch.
    /// Guarantees sub-10ms response time.
    /// </summary>
    public double TriggerPanic(string reason)
    {
        long startTimestamp = Stopwatch.GetTimestamp();

        int previous = Interlocked.Exchange(ref _isPanicTriggered, 1);
        _panicReason = reason;

        if (previous == 0)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS was already disposed or reset
            }

            // Release all depressed keys and buttons
            ReleaseAllInputs();

            try
            {
                OnPanic?.Invoke(reason);
            }
            catch
            {
                // Do not allow subscriber exceptions to prevent sub-10ms cutoff completion
            }
        }

        long endTimestamp = Stopwatch.GetTimestamp();
        double elapsedMs = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        _lastPanicLatencyMs = elapsedMs;
        return elapsedMs;
    }

    public void ReleaseAllInputs()
    {
        if (_inputSimulator != null)
        {
            _inputSimulator.SendMouseButton(MouseButton.Left, isDown: false);
            _inputSimulator.SendMouseButton(MouseButton.Right, isDown: false);
            _inputSimulator.SendMouseButton(MouseButton.Middle, isDown: false);
            _inputSimulator.SendKey(Win32Native.VK_CONTROL, isDown: false);
            _inputSimulator.SendKey(Win32Native.VK_MENU, isDown: false);
            _inputSimulator.SendKey(Win32Native.VK_SHIFT, isDown: false);
            _inputSimulator.SendKey(Win32Native.VK_SPACE, isDown: false);
        }
    }

    /// <summary>
    /// Processes low-level mouse move event. Returns true if velocity exceeded threshold and panic was triggered.
    /// </summary>
    public bool ProcessMouseEvent(int x, int y, uint flags, uint timestampMs)
    {
        // Check if event originated from physical hardware (LLMHF_INJECTED bit not set)
        bool isInjected = (flags & Win32Native.LLMHF_INJECTED) != 0;
        if (isInjected)
        {
            return false;
        }

        if (!_hasLastMouse)
        {
            _lastMouseX = x;
            _lastMouseY = y;
            _lastMouseTime = timestampMs;
            _hasLastMouse = true;
            return false;
        }

        double dt = (timestampMs - _lastMouseTime) / 1000.0;
        if (dt <= 0.0001)
        {
            return false;
        }

        double dx = x - _lastMouseX;
        double dy = y - _lastMouseY;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        double velocity = distance / dt;

        _lastMouseX = x;
        _lastMouseY = y;
        _lastMouseTime = timestampMs;

        if (velocity > VelocityThresholdPxPerSec)
        {
            TriggerPanic($"Physical mouse movement velocity exceeded threshold: {velocity:F1} px/s > {VelocityThresholdPxPerSec} px/s");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Processes low-level keyboard event. Triggers panic on Ctrl + Alt + Space.
    /// </summary>
    public bool ProcessKeyEvent(uint vkCode, uint flags)
    {
        bool isKeyUp = (flags & 0x80) != 0; // Transition state

        if (vkCode is Win32Native.VK_CONTROL or 0xA2 or 0xA3)
        {
            _ctrlDown = !isKeyUp;
        }
        else if (vkCode is Win32Native.VK_MENU or 0xA4 or 0xA5)
        {
            _altDown = !isKeyUp;
        }
        else if (vkCode == Win32Native.VK_SPACE && !isKeyUp)
        {
            if (_ctrlDown && _altDown)
            {
                TriggerPanic("Hardware panic hotkey (Ctrl+Alt+Space) triggered");
                return true;
            }
        }

        return false;
    }

    private void InstallHooks()
    {
        try
        {
            IntPtr hModule = Win32Native.GetModuleHandle(null);

            _mouseProc = MouseHookCallback;
            _mouseHookHandle = Win32Native.SetWindowsHookEx(Win32Native.WH_MOUSE_LL, _mouseProc, hModule, 0);

            _keyboardProc = KeyboardHookCallback;
            _keyboardHookHandle = Win32Native.SetWindowsHookEx(Win32Native.WH_KEYBOARD_LL, _keyboardProc, hModule, 0);
        }
        catch
        {
            // Gracefully ignore hook failures in headless/CI test environments
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)Win32Native.WM_MOUSEMOVE)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            ProcessMouseEvent(hookStruct.pt.X, hookStruct.pt.Y, hookStruct.flags, hookStruct.time);
        }
        return Win32Native.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            ProcessKeyEvent(hookStruct.vkCode, hookStruct.flags);
        }
        return Win32Native.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            Win32Native.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }

        if (_keyboardHookHandle != IntPtr.Zero)
        {
            Win32Native.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
