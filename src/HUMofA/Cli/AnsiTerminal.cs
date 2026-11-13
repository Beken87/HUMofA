using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HUMofA.Diff;

namespace HUMofA.Cli;

/// <summary>
/// Zero-dependency VT100 / ANSI terminal engine.
/// Provides rich color markup, dynamic window width, word wrapping, history recall,
/// box drawing, tool card rendering, and syntax-highlighted diff previews.
/// </summary>
public sealed class AnsiTerminal : ITerminalConsole
{
    private const int MaxHistorySize = 1000;
    private readonly string? _historyFilePath;
    private readonly List<string> _history = new();
    private readonly bool _ansiSupported;

    // Win32 console constants
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    public int WindowWidth
    {
        get
        {
            try
            {
                return Console.WindowWidth > 20 ? Console.WindowWidth : 80;
            }
            catch
            {
                return 80;
            }
        }
    }

    public int WindowHeight
    {
        get
        {
            try
            {
                return Console.WindowHeight > 5 ? Console.WindowHeight : 24;
            }
            catch
            {
                return 24;
            }
        }
    }

    public bool IsInteractive
    {
        get
        {
            try
            {
                return !Console.IsInputRedirected && !Console.IsOutputRedirected;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsAnsiSupported => _ansiSupported;

    public AnsiTerminal(string? historyFilePath = null)
    {
        _historyFilePath = historyFilePath;
        _ansiSupported = InitializeConsoleMode();
        LoadHistory();
    }

    private static bool InitializeConsoleMode()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var handle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    if (GetConsoleMode(handle, out uint mode))
                    {
                        mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
                        return SetConsoleMode(handle, mode);
                    }
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!string.IsNullOrEmpty(_historyFilePath) && File.Exists(_historyFilePath))
            {
                var lines = File.ReadAllLines(_historyFilePath);
                _history.AddRange(lines.TakeLast(MaxHistorySize));
            }
        }
        catch
        {
            // Non-critical if history file fails to read
        }
    }

    private void SaveHistory()
    {
        try
        {
            if (!string.IsNullOrEmpty(_historyFilePath))
            {
                var dir = Path.GetDirectoryName(_historyFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(_historyFilePath, _history.TakeLast(MaxHistorySize));
            }
        }
        catch
        {
            // Non-critical if history file fails to write
        }
    }

    public void Write(string text, ConsoleColor? color = null)
    {
        if (color.HasValue)
        {
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = color.Value;
            Console.Write(text);
            Console.ForegroundColor = oldColor;
        }
        else
        {
            Console.Write(text);
        }
        try { Console.Out.Flush(); } catch { }
    }

    public void WriteLine(string? text = null, ConsoleColor? color = null)
    {
        if (text == null)
        {
            Console.WriteLine();
            return;
        }

        if (color.HasValue)
        {
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = color.Value;
            Console.WriteLine(text);
            Console.ForegroundColor = oldColor;
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    public void WriteAnsi(string ansiSequence)
    {
        if (_ansiSupported)
        {
            Console.Write(ParseMarkupToAnsi(ansiSequence, true));
        }
        else
        {
            Console.Write(StripAnsi(StripMarkup(ansiSequence)));
        }
        try { Console.Out.Flush(); } catch { }
    }

    public void ClearLine()
    {
        if (_ansiSupported)
        {
            Console.Write("\x1b[2K\r");
        }
        else
        {
            try
            {
                Console.Write("\r" + new string(' ', WindowWidth - 1) + "\r");
            }
            catch
            {
                Console.Write("\r");
            }
        }
    }

    public void MoveCursorUp(int lines = 1)
    {
        if (lines <= 0) return;
        if (_ansiSupported)
        {
            Console.Write($"\x1b[{lines}A");
        }
        else
        {
            try
            {
                Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - lines));
            }
            catch
            {
                // Fallback
            }
        }
    }

    public void ClearScreen()
    {
        if (_ansiSupported)
        {
            Console.Write("\x1b[2J\x1b[H");
        }
        else
        {
            try
            {
                Console.Clear();
            }
            catch
            {
                Console.WriteLine();
            }
        }
    }

    public void RenderMarkup(string markup)
    {
        var formatted = ParseMarkupToAnsi(markup, _ansiSupported);
        Console.Write(formatted);
    }

    public void RenderMarkupLine(string markup)
    {
        RenderMarkup(markup);
        Console.WriteLine();
    }

    public void RenderRule(string title = "", char lineChar = '─')
    {
        int width = WindowWidth;
        if (string.IsNullOrWhiteSpace(title))
        {
            RenderMarkupLine($"[dim]{new string(lineChar, Math.Max(10, width - 1))}[/]");
            return;
        }

        var visibleTitle = StripMarkup(title);
        int sideLen = Math.Max(2, (width - visibleTitle.Length - 4) / 2);
        string left = new string(lineChar, sideLen);
        string right = new string(lineChar, Math.Max(2, width - sideLen - visibleTitle.Length - 4));

        RenderMarkupLine($"[dim]{left}[/] [bold cyan]{title}[/] [dim]{right}[/]");
    }

    public void RenderStatusBadges(PromptBadgeContext context)
    {
        int width = WindowWidth;
        string dirBadge = $"[dim]📁[/] [cyan]{context.FormatShortWorkingDir()}[/]";
        string gitBadge = $"[dim][/] [green]{context.GitBranch}[/]";
        string modelBadge = $"[yellow]{context.ActiveModel}[/]";
        string sessionBadge = $"[magenta]sess:{context.FormatShortSessionId()}[/]";
        string tokensBadge = $"[dim]{context.FormatTokens()}[/]";

        string rawBadges = $"{context.FormatShortWorkingDir()} | {context.GitBranch} | {context.ActiveModel} | sess:{context.FormatShortSessionId()} | {context.FormatTokens()}";
        
        RenderMarkupLine($"[dim]╭─[/] [ [dim]📁[/] [cyan]{context.FormatShortWorkingDir()}[/] ] ── [ [dim][/] [green]{context.GitBranch}[/] ] ── [ [yellow]{context.ActiveModel}[/] ] ── [ [magenta]sess:{context.FormatShortSessionId()}[/] ] ── [ [dim]{context.FormatTokens()}[/] ] [dim]─╮[/]");
    }

    public void RenderCard(ToolExecutionCard card)
    {
        int width = Math.Min(WindowWidth, 90);
        string icon = card.IsSuccess ? "[green]✔[/]" : "[red]✖[/]";
        string statusText = card.IsSuccess
            ? $"[green]Completed in {card.ExecutionTimeMs}ms[/]"
            : $"[red]Failed ({card.ExecutionTimeMs}ms)[/]";

        string titleBar = $"┌─ ⚡ [bold cyan]{card.ToolName}[/] ─";
        int padLen = Math.Max(2, width - StripMarkup(titleBar).Length - 1);
        string topFrame = titleBar + new string('─', padLen) + "┐";

        RenderMarkupLine(topFrame);
        RenderMarkupLine($"│ [dim]Parameters:[/] {EscapeMarkup(card.Parameters)}");
        RenderMarkupLine($"│ [dim]Status:[/] {icon} {statusText}");

        var lines = card.GetFoldedLines();
        if (lines.Count > 0)
        {
            RenderMarkupLine($"│ [dim]Output ({lines.Count} lines):[/]");
            foreach (var line in lines)
            {
                string safeLine = line.Length > width - 6 ? line.Substring(0, width - 6) + "..." : line;
                RenderMarkupLine($"│   [dim]{EscapeMarkup(safeLine)}[/]");
            }
        }

        RenderMarkupLine("└" + new string('─', width - 2) + "┘");
    }

    public void RenderDiff(DiffDocument diff)
    {
        int width = Math.Min(WindowWidth, 96);
        string header = $"╭── 📁 [bold cyan]Proposed Change:[/] [yellow]{diff.FilePath}[/] ([green]+{diff.AdditionsCount}[/], [red]-{diff.DeletionsCount}[/]) ──";
        int padLen = Math.Max(2, width - StripMarkup(header).Length - 1);
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
                        RenderMarkupLine($"│ [green]{gutter}+ {EscapeMarkup(content)}[/]");
                        break;
                    case DiffLineType.Deletion:
                        RenderMarkupLine($"│ [red]{gutter}- {EscapeMarkup(content)}[/]");
                        break;
                    default:
                        RenderMarkupLine($"│ [dim]{gutter}  {EscapeMarkup(content)}[/]");
                        break;
                }
            }
        }

        RenderMarkupLine("╰" + new string('─', width - 2) + "╯");
    }

    public async Task<string?> ReadLineAsync(PromptOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PromptOptions();

        if (options.BadgeContext != null)
        {
            RenderStatusBadges(options.BadgeContext);
        }

        RenderMarkup($"[bold cyan]╰─[/] [bold yellow]{options.PromptText}[/]");

        if (!IsInteractive)
        {
            return await Task.Run(() => Console.ReadLine(), ct).ConfigureAwait(false);
        }

        var inputBuffer = new StringBuilder();
        int cursorPosition = 0;
        int historyIndex = _history.Count;
        string currentStash = string.Empty;
        var suggestions = options.AutocompleteSuggestions ?? Array.Empty<string>();
        List<string>? tabCycleMatches = null;
        int tabCycleIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(15, ct).ConfigureAwait(false);
                continue;
            }

            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                // Check if multiline continuation requested via backslash or Shift+Enter
                bool endsWithBackslash = inputBuffer.Length > 0 && inputBuffer[inputBuffer.Length - 1] == '\\';
                bool shiftPressed = (key.Modifiers & ConsoleModifiers.Shift) != 0;

                if (options.AllowMultiline && (endsWithBackslash || shiftPressed))
                {
                    if (endsWithBackslash)
                    {
                        inputBuffer.Remove(inputBuffer.Length - 1, 1);
                    }
                    inputBuffer.Append('\n');
                    Console.WriteLine();
                    RenderMarkup($"[dim]{options.ContinuationPrompt}[/]");
                    cursorPosition = inputBuffer.Length;
                    continue;
                }

                Console.WriteLine();
                var result = inputBuffer.ToString().Trim();
                if (!string.IsNullOrEmpty(result))
                {
                    _history.Add(result);
                    if (_history.Count > MaxHistorySize) _history.RemoveAt(0);
                    SaveHistory();
                }
                return result;
            }

            if (key.Key == ConsoleKey.Tab && suggestions.Count > 0)
            {
                var textSoFar = inputBuffer.ToString();
                var matches = suggestions.Where(s => s.StartsWith(textSoFar, StringComparison.OrdinalIgnoreCase)).ToList();

                if (matches.Count == 0 && textSoFar.EndsWith(' '))
                {
                    var trimmed = textSoFar.Trim();
                    matches = suggestions.Where(s => s.StartsWith(trimmed + " ", StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (matches.Count > 0)
                {
                    if (tabCycleMatches != null && tabCycleMatches.Count > 0 &&
                        tabCycleMatches.SequenceEqual(matches, StringComparer.OrdinalIgnoreCase))
                    {
                        tabCycleIndex = (tabCycleIndex + 1) % tabCycleMatches.Count;
                    }
                    else
                    {
                        tabCycleMatches = matches;
                        tabCycleIndex = 0;
                    }

                    var chosen = tabCycleMatches[tabCycleIndex];
                    ClearCurrentInputLine(options.PromptText, inputBuffer.Length, cursorPosition);
                    inputBuffer.Clear().Append(chosen);
                    cursorPosition = chosen.Length;
                    Console.Write(chosen);
                }
                continue;
            }
            else
            {
                tabCycleMatches = null;
                tabCycleIndex = 0;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                if (_history.Count == 0) continue;
                if (historyIndex == _history.Count)
                {
                    currentStash = inputBuffer.ToString();
                }

                if (historyIndex > 0)
                {
                    historyIndex--;
                    ClearCurrentInputLine(options.PromptText, inputBuffer.Length, cursorPosition);
                    inputBuffer.Clear().Append(_history[historyIndex]);
                    cursorPosition = inputBuffer.Length;
                    Console.Write(inputBuffer.ToString());
                }
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                if (historyIndex < _history.Count - 1)
                {
                    historyIndex++;
                    ClearCurrentInputLine(options.PromptText, inputBuffer.Length, cursorPosition);
                    inputBuffer.Clear().Append(_history[historyIndex]);
                    cursorPosition = inputBuffer.Length;
                    Console.Write(inputBuffer.ToString());
                }
                else if (historyIndex == _history.Count - 1)
                {
                    historyIndex = _history.Count;
                    ClearCurrentInputLine(options.PromptText, inputBuffer.Length, cursorPosition);
                    inputBuffer.Clear().Append(currentStash);
                    cursorPosition = inputBuffer.Length;
                    Console.Write(inputBuffer.ToString());
                }
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursorPosition > 0)
                {
                    cursorPosition--;
                    Console.Write("\b");
                }
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursorPosition < inputBuffer.Length)
                {
                    Console.Write(inputBuffer[cursorPosition]);
                    cursorPosition++;
                }
                continue;
            }

            if (key.Key == ConsoleKey.Home)
            {
                while (cursorPosition > 0)
                {
                    Console.Write("\b");
                    cursorPosition--;
                }
                continue;
            }

            if (key.Key == ConsoleKey.End)
            {
                while (cursorPosition < inputBuffer.Length)
                {
                    Console.Write(inputBuffer[cursorPosition]);
                    cursorPosition++;
                }
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (cursorPosition > 0)
                {
                    inputBuffer.Remove(cursorPosition - 1, 1);
                    cursorPosition--;
                    Console.Write("\b");
                    var remaining = inputBuffer.ToString().Substring(cursorPosition);
                    Console.Write(remaining + " ");
                    Console.Write(new string('\b', remaining.Length + 1));
                }
                continue;
            }

            if (key.Key == ConsoleKey.Delete)
            {
                if (cursorPosition < inputBuffer.Length)
                {
                    inputBuffer.Remove(cursorPosition, 1);
                    var remaining = inputBuffer.ToString().Substring(cursorPosition);
                    Console.Write(remaining + " ");
                    Console.Write(new string('\b', remaining.Length + 1));
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                inputBuffer.Insert(cursorPosition, key.KeyChar);
                cursorPosition++;
                var remainder = inputBuffer.ToString().Substring(cursorPosition - 1);
                Console.Write(remainder);
                if (remainder.Length > 1)
                {
                    Console.Write(new string('\b', remainder.Length - 1));
                }
            }
        }

        return null;
    }

    private void ClearCurrentInputLine(string promptText, int bufferLength, int cursorPosition)
    {
        while (cursorPosition > 0)
        {
            Console.Write("\b");
            cursorPosition--;
        }
        Console.Write(new string(' ', bufferLength));
        Console.Write(new string('\b', bufferLength));
    }

    public async Task<bool> ConfirmAsync(string prompt, bool defaultYes = false, CancellationToken ct = default)
    {
        string hint = defaultYes ? "[Y/n]" : "[y/N]";
        RenderMarkup($"[bold yellow]{prompt}[/] [dim]{hint}[/] ");

        if (!IsInteractive)
        {
            var line = await Task.Run(() => Console.ReadLine(), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return defaultYes;
            return line.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }

        while (!ct.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine(defaultYes ? "yes" : "no");
                return defaultYes;
            }
            if (key.Key == ConsoleKey.Y)
            {
                Console.WriteLine("yes");
                return true;
            }
            if (key.Key == ConsoleKey.N)
            {
                Console.WriteLine("no");
                return false;
            }
        }

        return defaultYes;
    }

    public static string ResolveChoice(
        string? input,
        IReadOnlyList<string> options,
        string defaultOption)
    {
        if (options == null || options.Count == 0)
        {
            return defaultOption;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return defaultOption;
        }

        var trimmed = input.Trim();

        // 1. Exact match (case-insensitive)
        var exact = options.FirstOrDefault(o => o.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        // 2. Normalized synonyms for standard single-char prompt options
        bool hasY = options.Any(o => o.Equals("y", StringComparison.OrdinalIgnoreCase));
        bool hasN = options.Any(o => o.Equals("n", StringComparison.OrdinalIgnoreCase));
        bool hasD = options.Any(o => o.Equals("d", StringComparison.OrdinalIgnoreCase));
        bool hasA = options.Any(o => o.Equals("a", StringComparison.OrdinalIgnoreCase));

        // Rejection / No synonyms MUST be checked before any fallback so "no" NEVER resolves to "y"
        if (hasN && (trimmed.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("reject", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("deny", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("n", StringComparison.OrdinalIgnoreCase)))
        {
            return options.First(o => o.Equals("n", StringComparison.OrdinalIgnoreCase));
        }

        // Approval / Yes synonyms
        if (hasY && (trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("apply", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("approve", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("confirm", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)))
        {
            return options.First(o => o.Equals("y", StringComparison.OrdinalIgnoreCase));
        }

        // Details / Diff synonyms
        if (hasD && (trimmed.Equals("detail", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("details", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("diff", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("d", StringComparison.OrdinalIgnoreCase)))
        {
            return options.First(o => o.Equals("d", StringComparison.OrdinalIgnoreCase));
        }

        // Always / All synonyms
        if (hasA && (trimmed.Equals("always", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Equals("a", StringComparison.OrdinalIgnoreCase)))
        {
            return options.First(o => o.Equals("a", StringComparison.OrdinalIgnoreCase));
        }

        // 3. Prefix matching where option starts with input (e.g. option="rollback", input="roll")
        var prefixMatch = options.FirstOrDefault(o => o.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase));
        if (prefixMatch != null)
        {
            return prefixMatch;
        }

        // 4. Prefix matching where input starts with single-character option (e.g. input="nope", option="n")
        var charPrefixMatch = options.FirstOrDefault(o => o.Length == 1 && trimmed.StartsWith(o, StringComparison.OrdinalIgnoreCase));
        if (charPrefixMatch != null)
        {
            return charPrefixMatch;
        }

        return defaultOption;
    }

    public async Task<string> PromptChoiceAsync(
        string prompt,
        IReadOnlyList<string> options,
        string defaultOption,
        CancellationToken ct = default)
    {
        string optionsStr = string.Join("/", options);
        RenderMarkup($"[bold yellow]{prompt}[/] [dim]({optionsStr}) [default: {defaultOption}]:[/] ");

        if (!IsInteractive)
        {
            var line = await Task.Run(() => Console.ReadLine(), ct).ConfigureAwait(false);
            return ResolveChoice(line, options, defaultOption);
        }

        var buffer = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: false);
            if (key.Key == ConsoleKey.Enter)
            {
                var input = buffer.ToString();
                return ResolveChoice(input, options, defaultOption);
            }
            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }

        return defaultOption;
    }

    public static string ParseMarkupToAnsi(string markup, bool ansiEnabled = true)
    {
        if (string.IsNullOrEmpty(markup))
            return string.Empty;

        if (!ansiEnabled)
        {
            return StripMarkup(markup);
        }

        var sb = new StringBuilder();
        int i = 0;
        var styleStack = new Stack<string>();

        while (i < markup.Length)
        {
            if (markup[i] == '[')
            {
                if (i + 1 < markup.Length && markup[i + 1] == '[')
                {
                    sb.Append('[');
                    i += 2;
                    continue;
                }

                int closeIndex = markup.IndexOf(']', i);
                if (closeIndex > i)
                {
                    string tag = markup.Substring(i + 1, closeIndex - i - 1).Trim().ToLowerInvariant();
                    string? ansi = ResolveAnsiTag(tag, styleStack);
                    if (ansi != null)
                    {
                        sb.Append(ansi);
                        i = closeIndex + 1;
                        continue;
                    }
                }
            }

            sb.Append(markup[i]);
            i++;
        }

        return sb.ToString();
    }

    private static string? ResolveAnsiTag(string tag, Stack<string> styleStack)
    {
        if (tag == "/" || tag == "reset" || tag == "/reset")
        {
            styleStack.Clear();
            return "\x1b[0m";
        }

        if (tag.StartsWith("/"))
        {
            if (styleStack.Count > 0) styleStack.Pop();
            return "\x1b[0m" + ReapplyStyles(styleStack);
        }

        var parts = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            var combined = new StringBuilder();
            foreach (var part in parts)
            {
                var single = ResolveSingleAnsiCode(part);
                if (single != null)
                {
                    styleStack.Push(single);
                    combined.Append(single);
                }
            }
            return combined.Length > 0 ? combined.ToString() : null;
        }

        string? singleCode = ResolveSingleAnsiCode(tag);
        if (singleCode != null)
        {
            styleStack.Push(singleCode);
            return singleCode;
        }

        return null;
    }

    private static string? ResolveSingleAnsiCode(string tag)
    {
        return tag switch
        {
            "bold" => "\x1b[1m",
            "dim" => "\x1b[2m",
            "underline" => "\x1b[4m",
            "black" => "\x1b[30m",
            "red" => "\x1b[31m",
            "green" => "\x1b[32m",
            "yellow" => "\x1b[33m",
            "blue" => "\x1b[34m",
            "magenta" => "\x1b[35m",
            "cyan" => "\x1b[36m",
            "white" => "\x1b[37m",
            "gray" => "\x1b[90m",
            "bright-red" => "\x1b[91m",
            "bright-green" => "\x1b[92m",
            "bright-yellow" => "\x1b[93m",
            "bright-blue" => "\x1b[94m",
            "bright-magenta" => "\x1b[95m",
            "bright-cyan" => "\x1b[96m",
            "bright-white" => "\x1b[97m",
            "bg-red" => "\x1b[41m",
            "bg-green" => "\x1b[42m",
            "bg-yellow" => "\x1b[43m",
            "bg-blue" => "\x1b[44m",
            "bg-magenta" => "\x1b[45m",
            "bg-cyan" => "\x1b[46m",
            "bg-gray" => "\x1b[100m",
            _ => null
        };
    }

    private static string ReapplyStyles(Stack<string> stack)
    {
        if (stack.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var s in stack.Reverse())
        {
            sb.Append(s);
        }
        return sb.ToString();
    }

    public static string StripMarkup(string markup)
    {
        if (string.IsNullOrEmpty(markup)) return string.Empty;

        var sb = new StringBuilder();
        int i = 0;

        while (i < markup.Length)
        {
            if (markup[i] == '[')
            {
                if (i + 1 < markup.Length && markup[i + 1] == '[')
                {
                    sb.Append('[');
                    i += 2;
                    continue;
                }

                int closeIndex = markup.IndexOf(']', i);
                if (closeIndex > i)
                {
                    string tag = markup.Substring(i + 1, closeIndex - i - 1).Trim().ToLowerInvariant();
                    if (IsRecognizedMarkupTag(tag))
                    {
                        i = closeIndex + 1;
                        continue;
                    }
                }
            }

            sb.Append(markup[i]);
            i++;
        }

        return sb.ToString();
    }

    public static bool IsRecognizedMarkupTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        if (tag == "/" || tag == "reset" || tag == "/reset") return true;

        if (tag.StartsWith("/"))
        {
            var closingTag = tag.Substring(1).Trim();
            return IsRecognizedColorOrStyle(closingTag);
        }

        var parts = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        foreach (var part in parts)
        {
            if (!IsRecognizedColorOrStyle(part))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsRecognizedColorOrStyle(string name)
    {
        return name switch
        {
            "bold" or "dim" or "underline" => true,
            "black" or "red" or "green" or "yellow" or "blue" or "magenta" or "cyan" or "white" or "gray" => true,
            "bright-red" or "bright-green" or "bright-yellow" or "bright-blue" or "bright-magenta" or "bright-cyan" or "bright-white" => true,
            "bg-red" or "bg-green" or "bg-yellow" or "bg-blue" or "bg-magenta" or "bg-cyan" or "bg-gray" => true,
            _ => false
        };
    }

    public static string StripAnsi(string ansi)
    {
        if (string.IsNullOrEmpty(ansi)) return string.Empty;
        return Regex.Replace(ansi, @"\x1b\[[0-9;]*[a-zA-Z]", string.Empty);
    }

    public static string EscapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("[", "[[");
    }
}
