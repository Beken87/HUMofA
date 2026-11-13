namespace HUMofA.Diff;

/// <summary>
/// Contract for computing file diffs (Myers algorithm), unified diff formatting,
/// and creating/rolling back modification snapshots.
/// </summary>
public interface IFileDiffService
{
    DiffDocument ComputeDiff(string filePath, string oldContent, string newContent, int contextLines = 3);

    string FormatUnifiedDiff(DiffDocument diff);

    Task<SnapshotRecord> CreateSnapshotAsync(string filePath, CancellationToken ct = default);

    Task<bool> RollbackSnapshotAsync(string snapshotId, CancellationToken ct = default);

    Task<bool> RollbackLatestSnapshotAsync(string filePath, CancellationToken ct = default);

    Task<bool> ApplyDiffAsync(DiffDocument diff, bool createBackup = true, CancellationToken ct = default);

    IReadOnlyList<SnapshotRecord> GetSnapshots(string? filePath = null);
}
