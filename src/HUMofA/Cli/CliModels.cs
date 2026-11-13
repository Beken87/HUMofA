using HUMofA.Diff;

namespace HUMofA.Cli;

/// <summary>
/// Status badge context rendered above the interactive command line prompt.
/// </summary>
public sealed record PromptBadgeContext(
    string WorkingDirectory,
    string GitBranch,
    string ActiveModel,
    string SessionId,
    int TokenCount)
{
    public static PromptBadgeContext Default => new(
        WorkingDirectory: Directory.GetCurrentDirectory(),
        GitBranch: "main",
        ActiveModel: "gemini-2.5-flash",
        SessionId: "default",
        TokenCount: 0);

    public string FormatShortWorkingDir()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && WorkingDirectory.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            {
                var rel = WorkingDirectory.Substring(home.Length).Replace('\\', '/');
                if (rel.StartsWith('/')) rel = rel.Substring(1);
                return $"~/{rel}";
            }

            var dirName = Path.GetFileName(WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(dirName) ? WorkingDirectory : dirName;
        }
        catch
        {
            return WorkingDirectory;
        }
    }

    public string FormatTokens()
    {
        if (TokenCount >= 1_000_000)
            return $"{(TokenCount / 1_000_000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}M tokens";
        if (TokenCount >= 1000)
            return $"{(TokenCount / 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}k tokens";
        return $"{TokenCount} tokens";
    }

    public string FormatShortSessionId()
    {
        if (string.IsNullOrEmpty(SessionId))
            return "none";
        if (SessionId.StartsWith("sess_", StringComparison.OrdinalIgnoreCase) && SessionId.Length > 11)
            return SessionId.Substring(5, 6);
        return SessionId.Length > 8 ? SessionId.Substring(0, 8) : SessionId;
    }
}

/// <summary>
/// Configuration options for prompt presentation and line reading.
/// </summary>
public sealed record PromptOptions(
    PromptBadgeContext? BadgeContext = null,
    string PromptText = "HUMofA ❯ ",
    string ContinuationPrompt = "... ❯ ",
    bool AllowMultiline = true,
    IReadOnlyList<string>? AutocompleteSuggestions = null);

/// <summary>
/// Representation of a tool execution card rendered in the terminal.
/// </summary>
public sealed record ToolExecutionCard(
    string ToolName,
    string Parameters,
    string Output,
    long ExecutionTimeMs,
    bool IsSuccess,
    bool IsFolded = false)
{
    public const int MaxVisibleFoldLines = 8;

    public IReadOnlyList<string> GetFoldedLines()
    {
        var rawLines = (Output ?? string.Empty).Split('\n');
        if (rawLines.Length <= MaxVisibleFoldLines || !IsFolded)
            return rawLines;

        var result = new List<string>(MaxVisibleFoldLines + 1);
        for (int i = 0; i < 4 && i < rawLines.Length; i++)
            result.Add(rawLines[i]);

        int hiddenCount = rawLines.Length - 7;
        result.Add($"  ... ({hiddenCount} lines hidden, expand with /verbose or [d]) ...");

        for (int i = rawLines.Length - 3; i < rawLines.Length; i++)
        {
            if (i >= 0)
                result.Add(rawLines[i]);
        }

        return result;
    }
}

/// <summary>
/// User decision choice for file diff confirmation.
/// </summary>
public enum DiffConfirmationChoice
{
    Approve,
    Reject,
    Details,
    Always
}
