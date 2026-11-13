using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace HUMofA.Diff;

/// <summary>
/// File Diff engine implementing Eugene Myers' O(ND) difference algorithm,
/// unified diff formatting, and snapshot backup & rollback functionality.
/// </summary>
public sealed class FileDiffService : IFileDiffService
{
    public const string DefaultBackupDirectory = ".humofa-data/backups";

    private readonly string _backupDirectory;
    private readonly ConcurrentDictionary<string, SnapshotRecord> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public string BackupDirectory => _backupDirectory;

    public FileDiffService(string? backupDirectory = null)
    {
        _backupDirectory = string.IsNullOrWhiteSpace(backupDirectory)
            ? Path.GetFullPath(DefaultBackupDirectory)
            : Path.GetFullPath(backupDirectory);
    }

    public DiffDocument ComputeDiff(string filePath, string oldContent, string newContent, int contextLines = 3)
    {
        var oldLines = SplitIntoLines(oldContent);
        var newLines = SplitIntoLines(newContent);

        var editScript = RunMyersDiff(oldLines, newLines);

        var additions = editScript.Count(l => l.Type == DiffLineType.Addition);
        var deletions = editScript.Count(l => l.Type == DiffLineType.Deletion);

        var hunks = GroupIntoHunks(editScript, contextLines);

        return new DiffDocument(
            FilePath: filePath,
            OldContent: oldContent ?? string.Empty,
            NewContent: newContent ?? string.Empty,
            AdditionsCount: additions,
            DeletionsCount: deletions,
            Hunks: hunks);
    }

    public string FormatUnifiedDiff(DiffDocument diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (!diff.HasChanges || diff.Hunks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var normalizedPath = string.IsNullOrWhiteSpace(diff.FilePath) ? "file" : diff.FilePath.Replace('\\', '/');
        sb.AppendLine($"--- a/{normalizedPath}");
        sb.AppendLine($"+++ b/{normalizedPath}");

        foreach (var hunk in diff.Hunks)
        {
            var oldRange = hunk.OldCount == 1 ? $"{hunk.OldStart}" : $"{hunk.OldStart},{hunk.OldCount}";
            var newRange = hunk.NewCount == 1 ? $"{hunk.NewStart}" : $"{hunk.NewStart},{hunk.NewCount}";
            sb.AppendLine($"@@ -{oldRange} +{newRange} @@");

            foreach (var line in hunk.Lines)
            {
                switch (line.Type)
                {
                    case DiffLineType.Unchanged:
                        sb.AppendLine($" {line.Content}");
                        break;
                    case DiffLineType.Addition:
                        sb.AppendLine($"+{line.Content}");
                        break;
                    case DiffLineType.Deletion:
                        sb.AppendLine($"-{line.Content}");
                        break;
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<SnapshotRecord> CreateSnapshotAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Target file for snapshot does not exist: {fullPath}", fullPath);
        }

        Directory.CreateDirectory(_backupDirectory);

        var snapshotId = $"snap_{Guid.NewGuid():N}"[..16];
        var timestamp = DateTimeOffset.UtcNow;
        var fileName = Path.GetFileName(fullPath);
        var backupFileName = $"{timestamp:yyyyMMdd_HHmmss}_{snapshotId}_{fileName}";
        var backupPath = Path.Combine(_backupDirectory, backupFileName);

        var fileBytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

        await File.WriteAllBytesAsync(backupPath, fileBytes, ct).ConfigureAwait(false);

        var record = new SnapshotRecord(
            Id: snapshotId,
            FilePath: fullPath,
            SnapshotPath: backupPath,
            Timestamp: timestamp,
            ContentHash: hash,
            IsReverted: false);

        _snapshots[snapshotId] = record;
        return record;
    }

    public async Task<bool> RollbackSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
        {
            return false;
        }

        if (!File.Exists(snapshot.SnapshotPath))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(snapshot.FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var bytes = await File.ReadAllBytesAsync(snapshot.SnapshotPath, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(snapshot.FilePath, bytes, ct).ConfigureAwait(false);

        _snapshots[snapshotId] = snapshot with { IsReverted = true };
        return true;
    }

    public async Task<bool> RollbackLatestSnapshotAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var latest = _snapshots.Values
            .Where(s => string.Equals(s.FilePath, fullPath, StringComparison.OrdinalIgnoreCase) && !s.IsReverted)
            .OrderByDescending(s => s.Timestamp)
            .FirstOrDefault();

        if (latest is null)
        {
            return false;
        }

        return await RollbackSnapshotAsync(latest.Id, ct).ConfigureAwait(false);
    }

    public async Task<bool> ApplyDiffAsync(DiffDocument diff, bool createBackup = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentException.ThrowIfNullOrWhiteSpace(diff.FilePath);

        var fullPath = Path.GetFullPath(diff.FilePath);

        if (createBackup && File.Exists(fullPath))
        {
            await CreateSnapshotAsync(fullPath, ct).ConfigureAwait(false);
        }

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, diff.NewContent, Encoding.UTF8, ct).ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<SnapshotRecord> GetSnapshots(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return _snapshots.Values.OrderByDescending(s => s.Timestamp).ToList().AsReadOnly();
        }

        var fullPath = Path.GetFullPath(filePath);
        return _snapshots.Values
            .Where(s => string.Equals(s.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Timestamp)
            .ToList()
            .AsReadOnly();
    }

    private static string[] SplitIntoLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// Classic Eugene Myers O(ND) difference algorithm.
    /// Computes the shortest edit script (SES) between sequences a and b.
    /// </summary>
    private static List<DiffLine> RunMyersDiff(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;

        if (n == 0 && m == 0)
        {
            return [];
        }

        if (n == 0)
        {
            var pureAdds = new List<DiffLine>(m);
            for (var i = 0; i < m; i++)
            {
                pureAdds.Add(new DiffLine(null, i + 1, DiffLineType.Addition, b[i]));
            }
            return pureAdds;
        }

        if (m == 0)
        {
            var pureDels = new List<DiffLine>(n);
            for (var i = 0; i < n; i++)
            {
                pureDels.Add(new DiffLine(i + 1, null, DiffLineType.Deletion, a[i]));
            }
            return pureDels;
        }

        var max = n + m;
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();

        var foundEnd = false;
        for (var d = 0; d <= max; d++)
        {
            var vCopy = (int[])v.Clone();
            trace.Add(vCopy);

            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]))
                {
                    x = v[k + 1 + max]; // Move down (insertion)
                }
                else
                {
                    x = v[k - 1 + max] + 1; // Move right (deletion)
                }

                var y = x - k;

                // Snake (diagonal matching)
                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[k + max] = x;

                if (x >= n && y >= m)
                {
                    foundEnd = true;
                    break;
                }
            }

            if (foundEnd)
            {
                break;
            }
        }

        // Backtrack to assemble edit path
        var rawEdits = new List<(DiffLineType Type, string Content)>();
        var currX = n;
        var currY = m;

        for (var d = trace.Count - 1; d > 0; d--)
        {
            var prevV = trace[d];
            var k = currX - currY;

            int prevK;
            if (k == -d || (k != d && prevV[k - 1 + max] < prevV[k + 1 + max]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }

            var prevX = prevV[prevK + max];
            var prevY = prevX - prevK;

            while (currX > prevX && currY > prevY)
            {
                rawEdits.Add((DiffLineType.Unchanged, a[currX - 1]));
                currX--;
                currY--;
            }

            if (currX == prevX)
            {
                rawEdits.Add((DiffLineType.Addition, b[prevY]));
                currY--;
            }
            else
            {
                rawEdits.Add((DiffLineType.Deletion, a[prevX]));
                currX--;
            }
        }

        while (currX > 0 && currY > 0)
        {
            rawEdits.Add((DiffLineType.Unchanged, a[currX - 1]));
            currX--;
            currY--;
        }

        rawEdits.Reverse();

        // Assign line numbers
        var result = new List<DiffLine>(rawEdits.Count);
        var oldLineNo = 1;
        var newLineNo = 1;

        foreach (var (type, content) in rawEdits)
        {
            switch (type)
            {
                case DiffLineType.Unchanged:
                    result.Add(new DiffLine(oldLineNo++, newLineNo++, DiffLineType.Unchanged, content));
                    break;
                case DiffLineType.Deletion:
                    result.Add(new DiffLine(oldLineNo++, null, DiffLineType.Deletion, content));
                    break;
                case DiffLineType.Addition:
                    result.Add(new DiffLine(null, newLineNo++, DiffLineType.Addition, content));
                    break;
            }
        }

        return result;
    }

    private static IReadOnlyList<DiffHunk> GroupIntoHunks(List<DiffLine> lines, int contextLines)
    {
        var hasChanges = lines.Any(l => l.Type != DiffLineType.Unchanged);
        if (!hasChanges)
        {
            return [];
        }

        var changeIndices = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Type != DiffLineType.Unchanged)
            {
                changeIndices.Add(i);
            }
        }

        // Merge change groups within 2 * contextLines
        var ranges = new List<(int Start, int End)>();
        var currentStart = Math.Max(0, changeIndices[0] - contextLines);
        var currentEnd = Math.Min(lines.Count - 1, changeIndices[0] + contextLines);

        for (var i = 1; i < changeIndices.Count; i++)
        {
            var nextStart = Math.Max(0, changeIndices[i] - contextLines);
            var nextEnd = Math.Min(lines.Count - 1, changeIndices[i] + contextLines);

            if (nextStart <= currentEnd + 1)
            {
                currentEnd = Math.Max(currentEnd, nextEnd);
            }
            else
            {
                ranges.Add((currentStart, currentEnd));
                currentStart = nextStart;
                currentEnd = nextEnd;
            }
        }
        ranges.Add((currentStart, currentEnd));

        var hunks = new List<DiffHunk>(ranges.Count);
        foreach (var (start, end) in ranges)
        {
            var hunkLines = lines.GetRange(start, end - start + 1);

            var oldCount = hunkLines.Count(l => l.Type == DiffLineType.Unchanged || l.Type == DiffLineType.Deletion);
            var newCount = hunkLines.Count(l => l.Type == DiffLineType.Unchanged || l.Type == DiffLineType.Addition);

            var oldStart = hunkLines.FirstOrDefault(l => l.OldLineNo.HasValue)?.OldLineNo ?? 0;
            var newStart = hunkLines.FirstOrDefault(l => l.NewLineNo.HasValue)?.NewLineNo ?? 0;

            hunks.Add(new DiffHunk(
                OldStart: oldStart,
                OldCount: oldCount,
                NewStart: newStart,
                NewCount: newCount,
                Lines: hunkLines.AsReadOnly()));
        }

        return hunks.AsReadOnly();
    }
}
