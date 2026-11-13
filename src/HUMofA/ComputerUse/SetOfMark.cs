using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace HUMofA.ComputerUse;

public record SetOfMarkResult(
    byte[] ImageBytes,
    string Base64Image,
    IReadOnlyDictionary<int, UiElementInfo> MarkToElement,
    IReadOnlyList<UiElementInfo> Elements
);

public class SetOfMark
{
    private readonly IUIAutomationProvider _uiAutomation;
    private static readonly object _gdiLock = new();

    public SetOfMark(IUIAutomationProvider? uiAutomation = null)
    {
        _uiAutomation = uiAutomation ?? new UIAutomationTree();
    }

    /// <summary>
    /// Captures the screen, extracts interactive elements, overlays numbered badges [1], [2], etc.,
    /// and returns the marked-up image and mapping table.
    /// </summary>
    public SetOfMarkResult GenerateMarks(Bitmap? customBaseImage = null)
    {
        var elements = _uiAutomation.GetInteractiveElements();
        Bitmap bitmap = customBaseImage != null
            ? new Bitmap(customBaseImage)
            : CaptureScreen();

        var markMap = new Dictionary<int, UiElementInfo>();

        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using Font font = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
            using Brush bgBrush = new SolidBrush(Color.FromArgb(255, 238, 0)); // High-contrast Yellow
            using Pen borderPen = new Pen(Color.Black, 1.5f);
            using Brush textBrush = new SolidBrush(Color.Black);

            int markId = 1;
            foreach (var elem in elements)
            {
                markMap[markId] = elem;

                string label = $"[{markId}]";
                SizeF textSize = g.MeasureString(label, font);

                int badgeW = (int)Math.Ceiling(textSize.Width) + 6;
                int badgeH = (int)Math.Ceiling(textSize.Height) + 4;

                // Place badge near top-left of element bounds, clamped within bitmap dimensions
                int bx = Math.Clamp(elem.X, 0, Math.Max(0, bitmap.Width - badgeW));
                int by = Math.Clamp(elem.Y, 0, Math.Max(0, bitmap.Height - badgeH));

                var badgeRect = new Rectangle(bx, by, badgeW, badgeH);

                // Draw badge background & border
                g.FillRectangle(bgBrush, badgeRect);
                g.DrawRectangle(borderPen, badgeRect);

                // Draw label text
                g.DrawString(label, font, textBrush, bx + 3, by + 2);

                markId++;
            }
        }

        using var ms = new MemoryStream();
        lock (_gdiLock)
        {
            bitmap.Save(ms, ImageFormat.Png);
        }
        byte[] imageBytes = ms.ToArray();
        string base64 = Convert.ToBase64String(imageBytes);

        bitmap.Dispose();

        return new SetOfMarkResult(imageBytes, base64, markMap, elements);
    }

    /// <summary>
    /// Captures the desktop display using native GDI BitBlt.
    /// </summary>
    public static Bitmap CaptureScreen()
    {
        int width = Win32Native.GetSystemMetrics(Win32Native.SM_CXSCREEN);
        int height = Win32Native.GetSystemMetrics(Win32Native.SM_CYSCREEN);

        if (width <= 0) width = 1920;
        if (height <= 0) height = 1080;

        IntPtr hdcSrc = Win32Native.GetDC(IntPtr.Zero);
        IntPtr hdcDest = Win32Native.CreateCompatibleDC(hdcSrc);
        IntPtr hBitmap = Win32Native.CreateCompatibleBitmap(hdcSrc, width, height);
        IntPtr hOld = Win32Native.SelectObject(hdcDest, hBitmap);

        Win32Native.BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, Win32Native.SRCCOPY);

        Bitmap bitmap = Image.FromHbitmap(hBitmap);

        Win32Native.SelectObject(hdcDest, hOld);
        Win32Native.DeleteObject(hBitmap);
        Win32Native.DeleteDC(hdcDest);
        Win32Native.ReleaseDC(IntPtr.Zero, hdcSrc);

        return bitmap;
    }
}
