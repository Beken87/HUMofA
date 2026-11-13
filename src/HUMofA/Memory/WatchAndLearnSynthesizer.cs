using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HUMofA.Memory;

public sealed record RecordedDesktopAction(
    string ActionType,
    int? X = null,
    int? Y = null,
    string? Text = null,
    string? KeyOrShortcut = null,
    string? TargetWindow = null,
    int DelayMs = 120);

public sealed record SynthesizedSkillResult(
    string SkillName,
    string SkillFilePath,
    string MarkdownContent,
    int StepCount);

/// <summary>
/// "Watch & Learn" Macro-to-Skill Synthesizer.
/// Captures sequences of user or agent desktop actions and synthesizes them into agentskills.io-compatible SKILL.md files.
/// </summary>
public sealed class WatchAndLearnSynthesizer
{
    private readonly string _skillsOutputDirectory;
    private readonly List<RecordedDesktopAction> _activeRecording = new();
    private readonly object _sync = new();
    private bool _isRecording;

    public bool IsRecording
    {
        get { lock (_sync) return _isRecording; }
    }

    public WatchAndLearnSynthesizer(string skillsOutputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsOutputDirectory);
        _skillsOutputDirectory = Path.GetFullPath(skillsOutputDirectory);
        Directory.CreateDirectory(_skillsOutputDirectory);
    }

    public void StartRecording()
    {
        lock (_sync)
        {
            _activeRecording.Clear();
            _isRecording = true;
        }
    }

    public void RecordAction(RecordedDesktopAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            if (!_isRecording)
                throw new InvalidOperationException("Watch & Learn recorder is not currently active.");
            _activeRecording.Add(action);
        }
    }

    public IReadOnlyList<RecordedDesktopAction> StopRecording()
    {
        lock (_sync)
        {
            _isRecording = false;
            return _activeRecording.ToList();
        }
    }

    public async Task<SynthesizedSkillResult> SynthesizeSkillAsync(
        string skillName,
        string description,
        IReadOnlyList<RecordedDesktopAction> actions,
        IReadOnlyList<string>? triggerPhrases = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(actions);

        if (actions.Count == 0)
            throw new ArgumentException("At least one recorded action is required to synthesize a skill.", nameof(actions));

        var slug = Slugify(skillName);
        var skillFolder = Path.Combine(_skillsOutputDirectory, slug);
        Directory.CreateDirectory(skillFolder);

        var skillFilePath = Path.Combine(skillFolder, "SKILL.md");
        var triggers = triggerPhrases is { Count: > 0 }
            ? string.Join(", ", triggerPhrases)
            : slug.Replace('-', ' ');

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {slug}");
        sb.AppendLine($"description: \"{EscapeYaml(description)}\"");
        sb.AppendLine("version: 1.0.0");
        sb.AppendLine("source: watch-and-learn");
        sb.AppendLine($"created_at: \"{DateTimeOffset.UtcNow:O}\"");
        sb.AppendLine($"triggers: \"{EscapeYaml(triggers)}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {skillName.Trim()}");
        sb.AppendLine();
        sb.AppendLine(description.Trim());
        sb.AppendLine();
        sb.AppendLine("## Execution Steps");
        sb.AppendLine();

        for (int i = 0; i < actions.Count; i++)
        {
            var step = actions[i];
            sb.AppendLine($"{i + 1}. **{step.ActionType}**: {DescribeStep(step)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Replay Macro Payload");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(actions, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");

        var markdown = sb.ToString();
        await File.WriteAllTextAsync(skillFilePath, markdown, Encoding.UTF8, ct).ConfigureAwait(false);

        return new SynthesizedSkillResult(slug, skillFilePath, markdown, actions.Count);
    }

    private static string DescribeStep(RecordedDesktopAction step)
    {
        return step.ActionType.ToLowerInvariant() switch
        {
            "click" or "double_click" or "right_click" =>
                $"Perform `{step.ActionType}` at coordinates ({step.X ?? 0}, {step.Y ?? 0})" +
                (string.IsNullOrWhiteSpace(step.TargetWindow) ? "" : $" in window `{step.TargetWindow}`"),
            "type" =>
                $"Type humanized text \"{step.Text}\" (delay ~{step.DelayMs}ms)",
            "key" or "hotkey" =>
                $"Press key combination `{step.KeyOrShortcut}`",
            "focus_app" =>
                $"Focus application window `{step.TargetWindow}`",
            _ =>
                $"Execute `{step.ActionType}` ({step.Text ?? step.KeyOrShortcut ?? "custom"})"
        };
    }

    public static string Slugify(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        var cleaned = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(cleaned) ? "custom-skill" : cleaned;
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}

