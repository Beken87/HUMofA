using System.Text;

namespace HUMofA.Memory;

public enum MemoryDocumentType
{
    Soul,
    Memory,
    User
}

public sealed record ExternalImportSummary(
    string AgentType,
    string SourceDirectory,
    int ImportedFilesCount,
    IReadOnlyList<string> ImportedPaths);

/// <summary>
/// Hermes-compatible layered persistent memory store managing SOUL.md, MEMORY.md, USER.md,
/// cross-session FTS5 recall, and external agent memory migrations (OpenClaw, Claude Code, Cursor, Codex).
/// </summary>
public sealed class UnifiedMemoryStore : IDisposable
{
    private readonly string _memoryDirectory;
    private readonly Fts5SearchIndex _searchIndex;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _disposed;

    public string SoulFilePath => Path.Combine(_memoryDirectory, "SOUL.md");
    public string MemoryFilePath => Path.Combine(_memoryDirectory, "MEMORY.md");
    public string UserFilePath => Path.Combine(_memoryDirectory, "USER.md");
    public Fts5SearchIndex SearchIndex => _searchIndex;

    public UnifiedMemoryStore(string memoryDirectory, Fts5SearchIndex? searchIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryDirectory);
        _memoryDirectory = Path.GetFullPath(memoryDirectory);
        Directory.CreateDirectory(_memoryDirectory);

        var dbPath = Path.Combine(_memoryDirectory, "memory_fts5.db");
        _searchIndex = searchIndex ?? new Fts5SearchIndex(dbPath);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SoulFilePath))
            {
                await File.WriteAllTextAsync(SoulFilePath, GetDefaultSoulContent(), Encoding.UTF8, ct).ConfigureAwait(false);
            }
            if (!File.Exists(MemoryFilePath))
            {
                await File.WriteAllTextAsync(MemoryFilePath, GetDefaultMemoryContent(), Encoding.UTF8, ct).ConfigureAwait(false);
            }
            if (!File.Exists(UserFilePath))
            {
                await File.WriteAllTextAsync(UserFilePath, GetDefaultUserContent(), Encoding.UTF8, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _ioLock.Release();
        }

        await ReindexCoreFilesAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> ReadDocumentAsync(MemoryDocumentType type, CancellationToken ct = default)
    {
        var path = GetPathForType(type);
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return string.Empty;
            return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task WriteDocumentAsync(MemoryDocumentType type, string content, CancellationToken ct = default)
    {
        content ??= string.Empty;
        var path = GetPathForType(type);
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }

        await _searchIndex.UpsertDocumentAsync(
            id: $"core:{type.ToString().ToLowerInvariant()}",
            category: type.ToString().ToLowerInvariant(),
            title: $"{type}.md",
            content: content,
            source: path,
            ct: ct).ConfigureAwait(false);
    }

    public async Task AppendMemoryEntryAsync(
        MemoryDocumentType type,
        string sectionTitle,
        string entryText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryText);

        var path = GetPathForType(type);
        string updated;

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = File.Exists(path)
                ? await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false)
                : string.Empty;

            var sb = new StringBuilder(existing);
            if (sb.Length > 0 && !existing.EndsWith('\n'))
                sb.AppendLine();

            sb.AppendLine();
            sb.AppendLine($"## {sectionTitle.Trim()} ({DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)");
            sb.AppendLine(entryText.Trim());

            updated = sb.ToString();
            await File.WriteAllTextAsync(path, updated, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }

        // Index both the individual entry and the full document
        var entryId = $"entry:{type.ToString().ToLowerInvariant()}:{Guid.NewGuid():N}";
        await _searchIndex.UpsertDocumentAsync(
            id: entryId,
            category: type.ToString().ToLowerInvariant(),
            title: sectionTitle.Trim(),
            content: entryText.Trim(),
            source: path,
            ct: ct).ConfigureAwait(false);

        await _searchIndex.UpsertDocumentAsync(
            id: $"core:{type.ToString().ToLowerInvariant()}",
            category: type.ToString().ToLowerInvariant(),
            title: $"{type}.md",
            content: updated,
            source: path,
            ct: ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MemorySearchHit>> SearchMemoriesAsync(
        string query,
        string? categoryFilter = null,
        int limit = 10,
        CancellationToken ct = default)
    {
        return _searchIndex.SearchAsync(query, categoryFilter, limit, ct);
    }

    /// <summary>
    /// Imports persona, memories, and instructions from OpenClaw, Claude Code, Cursor, or Codex workspaces.
    /// </summary>
    public async Task<ExternalImportSummary> ImportFromExternalAgentAsync(
        string agentType,
        string sourceDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        if (!Directory.Exists(sourceDirectory))
        {
            return new ExternalImportSummary(agentType, sourceDirectory, 0, Array.Empty<string>());
        }

        var importedPaths = new List<string>();
        var candidateFiles = new (string RelativeName, MemoryDocumentType TargetType)[]
        {
            ("SOUL.md", MemoryDocumentType.Soul),
            ("MEMORY.md", MemoryDocumentType.Memory),
            ("USER.md", MemoryDocumentType.User),
            ("CLAUDE.md", MemoryDocumentType.Memory),
            ("AGENTS.md", MemoryDocumentType.Memory),
            (".cursorrules", MemoryDocumentType.Memory),
        };

        foreach (var (relativeName, targetType) in candidateFiles)
        {
            var fullPath = Path.Combine(sourceDirectory, relativeName);
            if (!File.Exists(fullPath))
                continue;

            var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            await AppendMemoryEntryAsync(
                targetType,
                $"Imported from {agentType} ({relativeName})",
                content,
                ct).ConfigureAwait(false);

            importedPaths.Add(fullPath);
        }

        return new ExternalImportSummary(agentType, sourceDirectory, importedPaths.Count, importedPaths);
    }

    private async Task ReindexCoreFilesAsync(CancellationToken ct)
    {
        foreach (var type in Enum.GetValues<MemoryDocumentType>())
        {
            var path = GetPathForType(type);
            if (!File.Exists(path))
                continue;

            var content = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            await _searchIndex.UpsertDocumentAsync(
                id: $"core:{type.ToString().ToLowerInvariant()}",
                category: type.ToString().ToLowerInvariant(),
                title: $"{type}.md",
                content: content,
                source: path,
                ct: ct).ConfigureAwait(false);
        }
    }

    private string GetPathForType(MemoryDocumentType type) => type switch
    {
        MemoryDocumentType.Soul => SoulFilePath,
        MemoryDocumentType.Memory => MemoryFilePath,
        MemoryDocumentType.User => UserFilePath,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static string GetDefaultSoulContent() => """
        # SOUL.md — HUMofA Core Identity
        You are HUMofA (Humanize of AI), an autonomous, trustworthy, and proactive right-hand PC assistant.
        You operate the desktop with human-like precision, respect safety boundaries, and retain compounding knowledge across sessions.
        """;

    private static string GetDefaultMemoryContent() => """
        # MEMORY.md — Long-Term Project & Problem-Solving Memory
        This file stores persistent architecture knowledge, solved task workflows, and environment configurations.
        """;

    private static string GetDefaultUserContent() => """
        # USER.md — Dialectic User Model & Preferences
        This file records user workflows, communication preferences, preferred tools, and recurring schedules.
        """;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _searchIndex.Dispose();
        _ioLock.Dispose();
    }
}

