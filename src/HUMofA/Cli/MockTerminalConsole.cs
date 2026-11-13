using System.Collections.Concurrent;
using System.Text;
using HUMofA.Diff;

namespace HUMofA.Cli;

/// <summary>
/// Headless test double implementing ITerminalConsole.
/// Provides deterministic input queuing and recorded output buffers for unit/integration testing.
/// </summary>
public sealed class MockTerminalConsole : ITerminalConsole
{
    private readonly ConcurrentQueue<string?> _inputQueue = new();
    private readonly ConcurrentQueue<bool> _confirmQueue = new();
    private readonly ConcurrentQueue<string> _choiceQueue = new();

    private readonly List<string> _outputLines = new();
    private readonly StringBuilder _rawOutput = new();
    private readonly StringBuilder _ansiStrippedOutput = new();
    private readonly List<ToolExecutionCard> _renderedCards = new();
    private readonly List<DiffDocument> _renderedDiffs = new();
    private readonly List<PromptBadgeContext> _renderedBadges = new();
    private readonly object _lock = new();

    public int WindowWidth { get; set; } = 100;
    public int WindowHeight { get; set; } = 30;
    public bool IsInteractive => true;
    public bool IsAnsiSupported => true;

    public IReadOnlyList<string> OutputLines
    {
        get
        {
            lock (_lock)
            {
                return _outputLines.ToList();
            }
        }
    }

    public string RawOutputText
    {
        get
        {
            lock (_lock)
            {
                return _rawOutput.ToString();
            }
        }
    }

    public string StrippedOutputText
    {
        get
        {
            lock (_lock)
            {
                return _ansiStrippedOutput.ToString();
            }
        }
    }

    public IReadOnlyList<ToolExecutionCard> RenderedCards
    {
        get
        {
            lock (_lock)
            {
                return _renderedCards.ToList();
            }
        }
    }

    public IReadOnlyList<DiffDocument> RenderedDiffs
    {
        get
        {
            lock (_lock)
            {
                return _renderedDiffs.ToList();
            }
        }
    }

    public IReadOnlyList<PromptBadgeContext> RenderedBadges
    {
        get
        {
            lock (_lock)
            {
                return _renderedBadges.ToList();
            }
        }
    }

    public void QueueInput(string line) => _inputQueue.Enqueue(line);
    public void QueueConfirmation(bool answer) => _confirmQueue.Enqueue(answer);
    public void QueueChoice(string choice) => _choiceQueue.Enqueue(choice);

    public void ClearOutput()
    {
        lock (_lock)
        {
            _outputLines.Clear();
            _rawOutput.Clear();
            _ansiStrippedOutput.Clear();
            _renderedCards.Clear();
            _renderedDiffs.Clear();
            _renderedBadges.Clear();
        }
    }

    public void Write(string text, ConsoleColor? color = null)
    {
        lock (_lock)
        {
            _rawOutput.Append(text);
            _ansiStrippedOutput.Append(AnsiTerminal.StripAnsi(text));
        }
    }

    public void WriteLine(string? text = null, ConsoleColor? color = null)
    {
        lock (_lock)
        {
            var line = text ?? string.Empty;
            _outputLines.Add(line);
            _rawOutput.AppendLine(line);
            _ansiStrippedOutput.AppendLine(AnsiTerminal.StripAnsi(line));
        }
    }

    public void WriteAnsi(string ansiSequence)
    {
        lock (_lock)
        {
            _rawOutput.Append(ansiSequence);
            _ansiStrippedOutput.Append(AnsiTerminal.StripAnsi(ansiSequence));
        }
    }

    public void ClearLine()
    {
        lock (_lock)
        {
            _rawOutput.Append("\r\x1b[2K");
        }
    }

    public void MoveCursorUp(int lines = 1)
    {
        lock (_lock)
        {
            _rawOutput.Append($"\x1b[{lines}A");
        }
    }

    public void ClearScreen()
    {
        lock (_lock)
        {
            _rawOutput.Append("\x1b[2J\x1b[H");
        }
    }

    public void RenderMarkup(string markup)
    {
        var ansi = AnsiTerminal.ParseMarkupToAnsi(markup, true);
        var stripped = AnsiTerminal.StripMarkup(markup);
        lock (_lock)
        {
            _rawOutput.Append(ansi);
            _ansiStrippedOutput.Append(stripped);
        }
    }

    public void RenderMarkupLine(string markup)
    {
        var ansi = AnsiTerminal.ParseMarkupToAnsi(markup, true);
        var stripped = AnsiTerminal.StripMarkup(markup);
        lock (_lock)
        {
            _outputLines.Add(stripped);
            _rawOutput.AppendLine(ansi);
            _ansiStrippedOutput.AppendLine(stripped);
        }
    }

    public void RenderRule(string title = "", char lineChar = '─')
    {
        int width = WindowWidth;
        if (string.IsNullOrWhiteSpace(title))
        {
            RenderMarkupLine($"[dim]{new string(lineChar, Math.Max(10, width - 1))}[/]");
            return;
        }

        var visibleTitle = AnsiTerminal.StripMarkup(title);
        int sideLen = Math.Max(2, (width - visibleTitle.Length - 4) / 2);
        string left = new string(lineChar, sideLen);
        string right = new string(lineChar, Math.Max(2, width - sideLen - visibleTitle.Length - 4));

        RenderMarkupLine($"[dim]{left}[/] [bold cyan]{title}[/] [dim]{right}[/]");
    }

    public void RenderStatusBadges(PromptBadgeContext context)
    {
        lock (_lock)
        {
            _renderedBadges.Add(context);
        }

        RenderMarkupLine($"[dim]╭─[/] [ [dim]📁[/] [cyan]{context.FormatShortWorkingDir()}[/] ] ── [ [dim][/] [green]{context.GitBranch}[/] ] ── [ [yellow]{context.ActiveModel}[/] ] ── [ [magenta]sess:{context.FormatShortSessionId()}[/] ] ── [ [dim]{context.FormatTokens()}[/] ] [dim]─╮[/]");
    }

    public void RenderCard(ToolExecutionCard card)
    {
        lock (_lock)
        {
            _renderedCards.Add(card);
        }

        int width = Math.Min(WindowWidth, 90);
        string icon = card.IsSuccess ? "[green]✔[/]" : "[red]✖[/]";
        string statusText = card.IsSuccess
            ? $"[green]Completed in {card.ExecutionTimeMs}ms[/]"
            : $"[red]Failed ({card.ExecutionTimeMs}ms)[/]";

        string titleBar = $"┌─ ⚡ [bold cyan]{card.ToolName}[/] ─";
        int padLen = Math.Max(2, width - AnsiTerminal.StripMarkup(titleBar).Length - 1);
        string topFrame = titleBar + new string('─', padLen) + "┐";

        RenderMarkupLine(topFrame);
        RenderMarkupLine($"│ [dim]Parameters:[/] {AnsiTerminal.EscapeMarkup(card.Parameters)}");
        RenderMarkupLine($"│ [dim]Status:[/] {icon} {statusText}");

        var lines = card.GetFoldedLines();
        if (lines.Count > 0)
        {
            RenderMarkupLine($"│ [dim]Output ({lines.Count} lines):[/]");
            foreach (var line in lines)
            {
                string safeLine = line.Length > width - 6 ? line.Substring(0, width - 6) + "..." : line;
                RenderMarkupLine($"│   [dim]{AnsiTerminal.EscapeMarkup(safeLine)}[/]");
            }
        }

        RenderMarkupLine("└" + new string('─', width - 2) + "┘");
    }

    public void RenderDiff(DiffDocument diff)
    {
        lock (_lock)
        {
            _renderedDiffs.Add(diff);
        }

        int width = Math.Min(WindowWidth, 96);
        string header = $"╭── 📁 [bold cyan]Proposed Change:[/] [yellow]{diff.FilePath}[/] ([green]+{diff.AdditionsCount}[/], [red]-{diff.DeletionsCount}[/]) ──";
        int padLen = Math.Max(2, width - AnsiTerminal.StripMarkup(header).Length - 1);
        RenderMarkupLine(header + new string('─', padLen) + "╮");

        foreach (var hunk in diff.Hunks)
        {
            RenderMarkupLine($"│ [bold cyan]@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@[/]");
            foreach (var line in hunk.Lines)
            {
                string oldNo = line.OldLineNo.HasValue ? line.OldLineNo.Value.ToString().PadLeft(4) : "    ";
                string newNo = line.NewLineNo.HasValue ? line.NewLineNo.Value.ToString().PadLeft(4) : "    ";
                string gutter = $"{oldNo} │ {newNo} │ ";

                string content = line.Content;
                if (content.Length > width - 18)
                {
                    content = content.Substring(0, width - 21) + "...";
                }

                switch (line.Type)
                {
                    case DiffLineType.Addition:
                        RenderMarkupLine($"│ [green]{gutter}+ {AnsiTerminal.EscapeMarkup(content)}[/]");
                        break;
                    case DiffLineType.Deletion:
                        RenderMarkupLine($"│ [red]{gutter}- {AnsiTerminal.EscapeMarkup(content)}[/]");
                        break;
                    default:
                        RenderMarkupLine($"│ [dim]{gutter}  {AnsiTerminal.EscapeMarkup(content)}[/]");
                        break;
                }
            }
        }

        RenderMarkupLine("╰" + new string('─', width - 2) + "╯");
    }

    public Task<string?> ReadLineAsync(PromptOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PromptOptions();
        if (options.BadgeContext != null)
        {
            RenderStatusBadges(options.BadgeContext);
        }

        RenderMarkup($"[bold cyan]╰─[/] [bold yellow]{options.PromptText}[/]");

        if (_inputQueue.TryDequeue(out var line))
        {
            return Task.FromResult(line);
        }

        return Task.FromResult<string?>(null);
    }

    public Task<bool> ConfirmAsync(string prompt, bool defaultYes = false, CancellationToken ct = default)
    {
        string hint = defaultYes ? "[Y/n]" : "[y/N]";
        RenderMarkupLine($"[bold yellow]{prompt}[/] [dim]{hint}[/]");

        if (_confirmQueue.TryDequeue(out var answer))
        {
            return Task.FromResult(answer);
        }

        return Task.FromResult(defaultYes);
    }

    public Task<string> PromptChoiceAsync(
        string prompt,
        IReadOnlyList<string> options,
        string defaultOption,
        CancellationToken ct = default)
    {
        string optionsStr = string.Join("/", options);
        RenderMarkupLine($"[bold yellow]{prompt}[/] [dim]({optionsStr}) [default: {defaultOption}]:[/]");

        if (_choiceQueue.TryDequeue(out var choice))
        {
            return Task.FromResult(AnsiTerminal.ResolveChoice(choice, options, defaultOption));
        }

        return Task.FromResult(defaultOption);
    }
}
