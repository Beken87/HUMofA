using System;
using System.Collections.Generic;

namespace HUMofA.ComputerUse;

public enum SimulatedEventType
{
    MouseMove,
    MouseDown,
    MouseUp,
    MouseWheel,
    KeyDown,
    KeyUp,
    Char
}

public record SimulatedInputEvent(
    SimulatedEventType EventType,
    int X,
    int Y,
    MouseButton? Button,
    ushort? VirtualKey,
    char? Character,
    int DeltaX,
    int DeltaY,
    DateTime Timestamp
);

public class MockInputSimulator : IInputSimulator
{
    private readonly List<SimulatedInputEvent> _events = [];
    private readonly object _lock = new();

    private int _cursorX;
    private int _cursorY;
    private int _screenWidth;
    private int _screenHeight;

    public MockInputSimulator(int initialX = 0, int initialY = 0, int screenWidth = 1920, int screenHeight = 1080)
    {
        _cursorX = initialX;
        _cursorY = initialY;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    public IReadOnlyList<SimulatedInputEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }

    public void SetCursorPosition(int x, int y)
    {
        lock (_lock)
        {
            _cursorX = x;
            _cursorY = y;
        }
    }

    public void SetScreenBounds(int width, int height)
    {
        lock (_lock)
        {
            _screenWidth = width;
            _screenHeight = height;
        }
    }

    public void SendMouseMove(int x, int y)
    {
        lock (_lock)
        {
            _cursorX = x;
            _cursorY = y;
            _events.Add(new SimulatedInputEvent(
                SimulatedEventType.MouseMove,
                x,
                y,
                null,
                null,
                null,
                0,
                0,
                DateTime.UtcNow
            ));
        }
    }

    public void SendMouseButton(MouseButton button, bool isDown)
    {
        lock (_lock)
        {
            _events.Add(new SimulatedInputEvent(
                isDown ? SimulatedEventType.MouseDown : SimulatedEventType.MouseUp,
                _cursorX,
                _cursorY,
                button,
                null,
                null,
                0,
                0,
                DateTime.UtcNow
            ));
        }
    }

    public void SendMouseWheel(int deltaX, int deltaY)
    {
        lock (_lock)
        {
            _events.Add(new SimulatedInputEvent(
                SimulatedEventType.MouseWheel,
                _cursorX,
                _cursorY,
                null,
                null,
                null,
                deltaX,
                deltaY,
                DateTime.UtcNow
            ));
        }
    }

    public void SendKey(ushort virtualKey, bool isDown)
    {
        lock (_lock)
        {
            _events.Add(new SimulatedInputEvent(
                isDown ? SimulatedEventType.KeyDown : SimulatedEventType.KeyUp,
                _cursorX,
                _cursorY,
                null,
                virtualKey,
                null,
                0,
                0,
                DateTime.UtcNow
            ));
        }
    }

    public void SendChar(char c)
    {
        lock (_lock)
        {
            _events.Add(new SimulatedInputEvent(
                SimulatedEventType.Char,
                _cursorX,
                _cursorY,
                null,
                null,
                c,
                0,
                0,
                DateTime.UtcNow
            ));
        }
    }

    public (int X, int Y) GetCursorPosition()
    {
        lock (_lock)
        {
            return (_cursorX, _cursorY);
        }
    }

    public (int Width, int Height) GetScreenBounds()
    {
        lock (_lock)
        {
            return (_screenWidth, _screenHeight);
        }
    }
}
