using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HUMofA.Desktop.Native;

/// <summary>
/// Pure C# WPF Design System & Theme Tokens inspired by Linear, Raycast, and Cursor.
/// Supports Deep Obsidian Dark and Clean Slate Light palettes.
/// </summary>
public sealed class NativeTheme
{
    public bool IsDark { get; private set; } = true;

    public SolidColorBrush BgPrimary { get; private set; } = BrushFromHex("#090D16");
    public SolidColorBrush BgSecondary { get; private set; } = BrushFromHex("#0F172A");
    public SolidColorBrush BgSurface { get; private set; } = BrushFromHex("#151F32");
    public SolidColorBrush BgElevated { get; private set; } = BrushFromHex("#1E293B");
    public SolidColorBrush BgHover { get; private set; } = BrushFromHex("#26354D");

    public SolidColorBrush BorderSubtle { get; private set; } = BrushFromHex("#1E293B");
    public SolidColorBrush BorderStrong { get; private set; } = BrushFromHex("#334155");
    public SolidColorBrush BorderAccent { get; private set; } = BrushFromHex("#38BDF8");

    public SolidColorBrush TextPrimary { get; private set; } = BrushFromHex("#F8FAFC");
    public SolidColorBrush TextSecondary { get; private set; } = BrushFromHex("#94A3B8");
    public SolidColorBrush TextMuted { get; private set; } = BrushFromHex("#64748B");

    public SolidColorBrush AccentPrimary { get; private set; } = BrushFromHex("#0EA5E9");
    public SolidColorBrush AccentGlow { get; private set; } = BrushFromHex("#38BDF8");
    public SolidColorBrush AccentSoftBg { get; private set; } = BrushFromHex("#0C2D48");

    public SolidColorBrush Success { get; private set; } = BrushFromHex("#10B981");
    public SolidColorBrush Warning { get; private set; } = BrushFromHex("#F59E0B");
    public SolidColorBrush Danger { get; private set; } = BrushFromHex("#EF4444");

    public FontFamily FontUi { get; } = new("Segoe UI Variable Display, Segoe UI, sans-serif");
    public FontFamily FontMono { get; } = new("Cascadia Code, Consolas, monospace");

    public NativeTheme(string themeName = "dark")
    {
        SetTheme(themeName);
    }

    public void SetTheme(string themeName)
    {
        IsDark = !string.Equals(themeName, "light", StringComparison.OrdinalIgnoreCase);

        if (IsDark)
        {
            BgPrimary = BrushFromHex("#090D16");
            BgSecondary = BrushFromHex("#0F172A");
            BgSurface = BrushFromHex("#151F32");
            BgElevated = BrushFromHex("#1E293B");
            BgHover = BrushFromHex("#26354D");

            BorderSubtle = BrushFromHex("#1E293B");
            BorderStrong = BrushFromHex("#334155");
            BorderAccent = BrushFromHex("#38BDF8");

            TextPrimary = BrushFromHex("#F8FAFC");
            TextSecondary = BrushFromHex("#94A3B8");
            TextMuted = BrushFromHex("#64748B");

            AccentPrimary = BrushFromHex("#0284C7");
            AccentGlow = BrushFromHex("#38BDF8");
            AccentSoftBg = BrushFromHex("#0C2A44");
        }
        else
        {
            BgPrimary = BrushFromHex("#F8FAFC");
            BgSecondary = BrushFromHex("#F1F5F9");
            BgSurface = BrushFromHex("#FFFFFF");
            BgElevated = BrushFromHex("#E2E8F0");
            BgHover = BrushFromHex("#CBD5E1");

            BorderSubtle = BrushFromHex("#E2E8F0");
            BorderStrong = BrushFromHex("#CBD5E1");
            BorderAccent = BrushFromHex("#0284C7");

            TextPrimary = BrushFromHex("#0F172A");
            TextSecondary = BrushFromHex("#475569");
            TextMuted = BrushFromHex("#64748B");

            AccentPrimary = BrushFromHex("#0284C7");
            AccentGlow = BrushFromHex("#0369A1");
            AccentSoftBg = BrushFromHex("#E0F2FE");
        }
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public Button CreateButton(
        string text,
        bool isPrimary = false,
        bool isDanger = false,
        Thickness? padding = null,
        double fontSize = 12.5)
    {
        var bg = isDanger
            ? BrushFromHex("#991B1B")
            : isPrimary
                ? AccentPrimary
                : BgElevated;

        var fg = (isPrimary || isDanger)
            ? BrushFromHex("#FFFFFF")
            : TextPrimary;

        var borderBrush = isDanger
            ? Danger
            : isPrimary
                ? AccentGlow
                : BorderStrong;

        var btn = new Button
        {
            Content = text,
            FontFamily = FontUi,
            FontSize = fontSize,
            FontWeight = isPrimary || isDanger ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = fg,
            Background = bg,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(12, 6, 12, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            SnapsToDevicePixels = true
        };

        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "BtnBorder";
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        presenterFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenterFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenterFactory);

        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(
            Border.BackgroundProperty,
            isDanger ? Danger : (isPrimary ? AccentGlow : BgHover),
            "BtnBorder"));
        template.Triggers.Add(hoverTrigger);

        btn.Template = template;
        return btn;
    }

    public TextBox CreateTextBox(string initialText = "", string placeholder = "", bool isMonospace = false, bool isMultiline = false)
    {
        var tb = new TextBox
        {
            Text = initialText,
            Tag = placeholder,
            FontFamily = isMonospace ? FontMono : FontUi,
            FontSize = 13,
            Foreground = TextPrimary,
            Background = BgPrimary,
            CaretBrush = AccentGlow,
            BorderBrush = BorderStrong,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            AcceptsReturn = isMultiline,
            TextWrapping = isMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = isMultiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden
        };
        return tb;
    }

    public Border CreateCard(UIElement child, Thickness? padding = null, Thickness? margin = null, bool highlight = false)
    {
        return new Border
        {
            Background = BgSurface,
            BorderBrush = highlight ? BorderAccent : BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = padding ?? new Thickness(14),
            Margin = margin ?? new Thickness(0, 0, 0, 10),
            Child = child
        };
    }

    public Border CreatePill(string text, SolidColorBrush? fg = null, SolidColorBrush? bg = null)
    {
        return new Border
        {
            Background = bg ?? AccentSoftBg,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = FontUi,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg ?? AccentGlow
            }
        };
    }
}

