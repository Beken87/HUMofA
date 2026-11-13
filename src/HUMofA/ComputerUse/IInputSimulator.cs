namespace HUMofA.ComputerUse;

public enum MouseButton
{
    Left,
    Right,
    Middle,
    X1,
    X2
}

public interface IInputSimulator
{
    void SendMouseMove(int x, int y);
    void SendMouseButton(MouseButton button, bool isDown);
    void SendMouseWheel(int deltaX, int deltaY);
    void SendKey(ushort virtualKey, bool isDown);
    void SendChar(char c);
    (int X, int Y) GetCursorPosition();
    (int Width, int Height) GetScreenBounds();
}
