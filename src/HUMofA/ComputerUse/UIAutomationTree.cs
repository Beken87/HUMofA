using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HUMofA.ComputerUse;

public record UiElementInfo(
    int Id,
    string Name,
    string ControlType,
    int X,
    int Y,
    int Width,
    int Height,
    int CenterX,
    int CenterY,
    bool IsEnabled,
    string AutomationId = "",
    string ClassName = ""
);

public interface IUIAutomationProvider
{
    IReadOnlyList<UiElementInfo> GetInteractiveElements();
    UiElementInfo? FindElementById(int id);
    UiElementInfo? FindElementByName(string name);
}

[ComImport]
[Guid("FF48DB42-9CC8-47EE-974D-BDE0E0EC732A")]
[ClassInterface(ClassInterfaceType.None)]
[TypeLibType(TypeLibTypeFlags.FCanCreate)]
internal class CUIAutomation
{
}

[ComImport]
[Guid("30CBE57D-D9D0-452A-AB13-7AC0E1B1EC12")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    [PreserveSig]
    int CompareElements(IntPtr el1, IntPtr el2, out int areSame);
    [PreserveSig]
    int CompareRuntimeIds(IntPtr r1, IntPtr r2, out int areSame);
    [PreserveSig]
    int GetRootElement(out IntPtr root);
}

public class UIAutomationTree : IUIAutomationProvider
{
    private readonly List<UiElementInfo> _elements = [];
    private readonly List<UiElementInfo>? _mockElements;

    public UIAutomationTree(IEnumerable<UiElementInfo>? mockElements = null)
    {
        if (mockElements != null)
        {
            _mockElements = new List<UiElementInfo>(mockElements);
        }
    }

    public IReadOnlyList<UiElementInfo> GetInteractiveElements()
    {
        if (_mockElements != null)
        {
            return _mockElements;
        }

        var results = new List<UiElementInfo>();
        int idCounter = 1;

        // Enumerate visible windows via Win32 API
        try
        {
            Win32Native.EnumWindows((hWnd, lParam) =>
            {
                if (!Win32Native.IsWindowVisible(hWnd))
                {
                    return true;
                }

                int len = Win32Native.GetWindowTextLength(hWnd);
                if (len <= 0)
                {
                    return true;
                }

                var sb = new StringBuilder(len + 1);
                Win32Native.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();

                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                if (Win32Native.GetWindowRect(hWnd, out RECT rect))
                {
                    int w = rect.Width;
                    int h = rect.Height;

                    // Filter out zero-sized or offscreen windows
                    if (w > 10 && h > 10 && rect.Right > 0 && rect.Bottom > 0)
                    {
                        int cx = rect.Left + (w / 2);
                        int cy = rect.Top + (h / 2);

                        results.Add(new UiElementInfo(
                            Id: idCounter++,
                            Name: title,
                            ControlType: "Window",
                            X: rect.Left,
                            Y: rect.Top,
                            Width: w,
                            Height: h,
                            CenterX: cx,
                            CenterY: cy,
                            IsEnabled: true,
                            AutomationId: $"hwnd_{hWnd.ToInt64()}",
                            ClassName: "Window"
                        ));
                    }
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Fallback gracefully if running in restricted context
        }

        _elements.Clear();
        _elements.AddRange(results);
        return results;
    }

    public UiElementInfo? FindElementById(int id)
    {
        var elements = _mockElements ?? (IReadOnlyList<UiElementInfo>)_elements;
        return elements.FirstOrDefault(e => e.Id == id);
    }

    public UiElementInfo? FindElementByName(string name)
    {
        var elements = _mockElements ?? (IReadOnlyList<UiElementInfo>)_elements;
        return elements.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                            e.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }
}
