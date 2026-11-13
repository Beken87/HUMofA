namespace HUMofA.Diff;

/// <summary>
/// Type of difference line in a diff hunk.
/// </summary>
public enum DiffLineType
{
    Unchanged,
    Addition,
    Deletion
}

/// <summary>
/// Single line in a diff hunk with source and target line numbering.
/// </summary>
public sealed record DiffLine(
    int? OldLineNo,
    int? NewLineNo,
    DiffLineType Type,
    string Content);

/// <summary>
/// Hunk of differences surrounded by unchanged context lines.
/// </summary>
public sealed record DiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<DiffLine> Lines);

/// <summary>
/// Diff document representing all hunks, line statistics, and original/new content.
/// </summary>
public sealed record DiffDocument(
    string FilePath,
    string OldContent,
    string NewContent,
    int AdditionsCount,
    int DeletionsCount,
    IReadOnlyList<DiffHunk> Hunks)
{
    public bool HasChanges => AdditionsCount > 0 || DeletionsCount > 0;
}

/// <summary>
/// Metadata for a timestamped file modification rollback snapshot.
/// </summary>
public sealed record SnapshotRecord(
    string Id,
    string FilePath,
    string SnapshotPath,
    DateTimeOffset Timestamp,
    string ContentHash,
    bool IsReverted = false);
