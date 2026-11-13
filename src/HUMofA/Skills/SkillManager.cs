using System.Collections.Concurrent;
using System.Text;
using HUMofA.Memory;

namespace HUMofA.Skills;

/// <summary>
/// Manages local installed skills, autonomous self-improving skill synthesis, FTS5 skill indexing,
/// and installation from the GitHub Skills Hub with mandatory SkillGuard verification.
/// </summary>
public sealed class SkillManager
{
    private readonly string _skillsDirectory;
    private readonly ISkillGuard _skillGuard;
    private readonly SkillsHubGitHub _skillsHub;
    private readonly Fts5SearchIndex? _searchIndex;
    private readonly ConcurrentDictionary<string, SkillDefinition> _installedSkills = new(StringComparer.OrdinalIgnoreCase);

    public string SkillsDirectory => _skillsDirectory;
    public IReadOnlyDictionary<string, SkillDefinition> InstalledSkills => _installedSkills;

    public SkillManager(
        string skillsDirectory,
        ISkillGuard? skillGuard = null,
        SkillsHubGitHub? skillsHub = null,
        Fts5SearchIndex? searchIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsDirectory);
        _skillsDirectory = Path.GetFullPath(skillsDirectory);
        Directory.CreateDirectory(_skillsDirectory);

        _skillGuard = skillGuard ?? new SkillGuard();
        _skillsHub = skillsHub ?? new SkillsHubGitHub(_skillGuard);
        _searchIndex = searchIndex;
    }

    public async Task<int> LoadInstalledSkillsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_skillsDirectory))
            return 0;

        var skillFiles = Directory.EnumerateFiles(_skillsDirectory, "SKILL.md", SearchOption.AllDirectories);
        var loaded = 0;

        foreach (var file in skillFiles)
        {
            var scan = await _skillGuard.ScanSkillFileAsync(file, ct).ConfigureAwait(false);
            if (!scan.IsSafe)
                continue;

            var markdown = await File.ReadAllTextAsync(file, Encoding.UTF8, ct).ConfigureAwait(false);
            var def = await _skillsHub.ParseAndVerifySkillAsync(markdown, "local", file, "installed", ct).ConfigureAwait(false);
            _installedSkills[def.Name] = def;
            loaded++;

            if (_searchIndex is not null)
            {
                await _searchIndex.UpsertDocumentAsync(
                    id: $"skill:{def.Name}",
                    category: "skill",
                    title: def.Name,
                    content: $"{def.Description}\n{def.RawMarkdown}",
                    source: file,
                    ct: ct).ConfigureAwait(false);
            }
        }

        return loaded;
    }

    public async Task<SkillDefinition> InstallSkillFromGitHubAsync(
        string repoOrUrl,
        string skillSubPath = "SKILL.md",
        CancellationToken ct = default)
    {
        var skill = await _skillsHub.FetchSkillFromGitHubAsync(repoOrUrl, skillSubPath, ct).ConfigureAwait(false);
        return await SaveAndRegisterSkillAsync(skill, ct).ConfigureAwait(false);
    }

    public async Task<SkillDefinition> InstallSkillFromMarkdownAsync(
        string rawMarkdown,
        string provider = "custom",
        string repository = "local",
        string category = "custom",
        CancellationToken ct = default)
    {
        var skill = await _skillsHub.ParseAndVerifySkillAsync(rawMarkdown, provider, repository, category, ct).ConfigureAwait(false);
        return await SaveAndRegisterSkillAsync(skill, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Autonomous Closed Learning Loop: Creates or refines a SKILL.md after resolving a complex task.
    /// </summary>
    public async Task<SkillDefinition> AutoGenerateSkillFromTaskAsync(
        string skillName,
        string taskProblemSummary,
        IReadOnlyList<string> solutionSteps,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskProblemSummary);
        ArgumentNullException.ThrowIfNull(solutionSteps);

        var slug = WatchAndLearnSynthesizer.Slugify(skillName);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {slug}");
        sb.AppendLine($"description: \"{taskProblemSummary.Replace("\"", "'")}\"");
        sb.AppendLine("provider: HUMofA-SelfLearning");
        sb.AppendLine($"updated_at: \"{DateTimeOffset.UtcNow:O}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {skillName.Trim()}");
        sb.AppendLine();
        sb.AppendLine("## Problem Context");
        sb.AppendLine(taskProblemSummary.Trim());
        sb.AppendLine();
        sb.AppendLine("## Verified Resolution Workflow");
        for (int i = 0; i < solutionSteps.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {solutionSteps[i]}");
        }

        return await InstallSkillFromMarkdownAsync(
            sb.ToString(),
            provider: "HUMofA-SelfLearning",
            repository: "local/auto-generated",
            category: "autonomous-ai-agents",
            ct: ct).ConfigureAwait(false);
    }

    private async Task<SkillDefinition> SaveAndRegisterSkillAsync(SkillDefinition skill, CancellationToken ct)
    {
        var slug = WatchAndLearnSynthesizer.Slugify(skill.Name);
        var targetFolder = Path.Combine(_skillsDirectory, slug);
        Directory.CreateDirectory(targetFolder);

        var filePath = Path.Combine(targetFolder, "SKILL.md");
        await File.WriteAllTextAsync(filePath, skill.RawMarkdown, Encoding.UTF8, ct).ConfigureAwait(false);

        _installedSkills[skill.Name] = skill;

        if (_searchIndex is not null)
        {
            await _searchIndex.UpsertDocumentAsync(
                id: $"skill:{skill.Name}",
                category: "skill",
                title: skill.Name,
                content: $"{skill.Description}\n{skill.RawMarkdown}",
                source: filePath,
                ct: ct).ConfigureAwait(false);
        }

        return skill;
    }
}

