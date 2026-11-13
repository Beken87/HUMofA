using System;
using System.Runtime.InteropServices;

namespace HUMofA.ComputerUse;

public class Win32InputSimulator : IInputSimulator
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public void SendMouseMove(int x, int y)
    {
        var (width, height) = GetScreenBounds();
        int normX = (int)Math.Round((x * 65535.0) / Math.Max(1, width - 1));
        int normY = (int)Math.Round((y * 65535.0) / Math.Max(1, height - 1));

        var input = new INPUT
        {
            type = InputType.Mouse,
            mi = new MOUSEINPUT
            {
                dx = normX,
                dy = normY,
                dwFlags = MouseEventFlags.Move | MouseEventFlags.Absolute | MouseEventFlags.VirtualDesk,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        };

        Win32Native.SendInput(1, [input], InputSize);
    }

    public void SendMouseButton(MouseButton button, bool isDown)
    {
        MouseEventFlags flag = button switch
        {
            MouseButton.Left => isDown ? MouseEventFlags.LeftDown : MouseEventFlags.LeftUp,
            MouseButton.Right => isDown ? MouseEventFlags.RightDown : MouseEventFlags.RightUp,
            MouseButton.Middle => isDown ? MouseEventFlags.MiddleDown : MouseEventFlags.MiddleUp,
            MouseButton.X1 => isDown ? MouseEventFlags.XDown : MouseEventFlags.XUp,
            MouseButton.X2 => isDown ? MouseEventFlags.XDown : MouseEventFlags.XUp,
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };

        uint mouseData = button switch
        {
            MouseButton.X1 => 1,
            MouseButton.X2 => 2,
            _ => 0
        };

        var input = new INPUT
        {
            type = InputType.Mouse,
            mi = new MOUSEINPUT
            {
                dx = 0,
                dy = 0,
                mouseData = mouseData,
                dwFlags = flag,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        };

        Win32Native.SendInput(1, [input], InputSize);
    }

    public void SendMouseWheel(int deltaX, int deltaY)
    {
        if (deltaY != 0)
        {
            var inputY = new INPUT
            {
                type = InputType.Mouse,
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = unchecked((uint)deltaY),
                    dwFlags = MouseEventFlags.Wheel,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            };
            Win32Native.SendInput(1, [inputY], InputSize);
        }

        if (deltaX != 0)
        {
            var inputX = new INPUT
            {
                type = InputType.Mouse,
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = unchecked((uint)deltaX),
                    dwFlags = MouseEventFlags.HWheel,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            };
            Win32Native.SendInput(1, [inputX], InputSize);
        }
    }

    public void SendKey(ushort virtualKey, bool isDown)
    {
        var flags = isDown ? KeyEventFlags.KeyDown : KeyEventFlags.KeyUp;
        var input = new INPUT
        {
            type = InputType.Keyboard,
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        };

        Win32Native.SendInput(1, [input], InputSize);
    }

    public void SendChar(char c)
    {
        var inputDown = new INPUT
        {
            type = InputType.Keyboard,
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KeyEventFlags.Unicode,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        };

        var inputUp = new INPUT
        {
            type = InputType.Keyboard,
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KeyEventFlags.Unicode | KeyEventFlags.KeyUp,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        };

        Win32Native.SendInput(2, [inputDown, inputUp], InputSize);
    }

    public (int X, int Y) GetCursorPosition()
    {
        if (Win32Native.GetCursorPos(out POINT pt))
        {
            return (pt.X, pt.Y);
        }
        return (0, 0);
    }

    public (int Width, int Height) GetScreenBounds()
    {
        int width = Win32Native.GetSystemMetrics(Win32Native.SM_CXSCREEN);
        int height = Win32Native.GetSystemMetrics(Win32Native.SM_CYSCREEN);
        return (width > 0 ? width : 1920, height > 0 ? height : 1080);
    }
}
