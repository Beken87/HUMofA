using HUMofA.Diff;

namespace HUMofA.Cli;

/// <summary>
/// Abstraction interface for VT100 / ANSI terminal interactions, rendering, and input reading.
/// Decouples CLI logic from System.Console for 100% deterministic testability.
/// </summary>
public interface ITerminalConsole
{
    int WindowWidth { get; }
    int WindowHeight { get; }
    bool IsInteractive { get; }
    bool IsAnsiSupported { get; }

    void Write(string text, ConsoleColor? color = null);
    void WriteLine(string? text = null, ConsoleColor? color = null);
    void WriteAnsi(string ansiSequence);
    void ClearLine();
    void MoveCursorUp(int lines = 1);
    void ClearScreen();

    // Rich markup and box rendering primitives
    void RenderMarkup(string markup);
    void RenderMarkupLine(string markup);
    void RenderRule(string title = "", char lineChar = '─');
    void RenderCard(ToolExecutionCard card);
    void RenderDiff(DiffDocument diff);
    void RenderStatusBadges(PromptBadgeContext context);

    // Interactive input primitives
    Task<string?> ReadLineAsync(PromptOptions? options = null, CancellationToken ct = default);
    Task<bool> ConfirmAsync(string prompt, bool defaultYes = false, CancellationToken ct = default);
    Task<string> PromptChoiceAsync(string prompt, IReadOnlyList<string> options, string defaultOption, CancellationToken ct = default);
}
