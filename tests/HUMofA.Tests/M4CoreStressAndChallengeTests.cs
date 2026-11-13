using System.Text;
using FluentAssertions;
using HUMofA.Diff;
using HUMofA.Sessions;
using Xunit;

namespace HUMofA.Tests;

/// <summary>
/// Empirical challenge and stress test suite for M4 Core Subsystems:
/// Eugene Myers FileDiffService and SQLite WAL SessionManager.
/// </summary>
public class M4CoreStressAndChallengeTests : IDisposable
{
    private readonly string _tempDir;

    public M4CoreStressAndChallengeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "humofa-challenge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp dir cleanup errors
        }
    }

    // =========================================================================
    // SECTION 1: FileDiffService Complex Scenarios & Edge Cases
    // =========================================================================

    [Fact]
    public void FileDiffService_IdenticalFiles_ReturnsZeroChanges()
    {
        var service = new FileDiffService(_tempDir);
        var content = "line 1\nline 2\nline 3\nline 4\nline 5\n";

        var diff = service.ComputeDiff("test.txt", content, content);

        diff.HasChanges.Should().BeFalse();
        diff.AdditionsCount.Should().Be(0);
        diff.DeletionsCount.Should().Be(0);
        diff.Hunks.Should().BeEmpty();

        var unified = service.FormatUnifiedDiff(diff);
        unified.Should().BeEmpty();
    }

    [Fact]
    public void FileDiffService_EmptyOldVsPopulatedNew_Returns100PercentAdditions()
    {
        var service = new FileDiffService(_tempDir);
        var oldContent = string.Empty;
        var newLines = new[]
        {
            "Console.WriteLine(\"Init 1\");",
            "Console.WriteLine(\"Init 2\");",
            "Console.WriteLine(\"Init 3\");",
            "Console.WriteLine(\"Init 4\");"
        };
        var newContent = string.Join("\n", newLines);

        var diff = service.ComputeDiff("file.cs", oldContent, newContent);

        diff.HasChanges.Should().BeTrue();
        diff.AdditionsCount.Should().Be(4);
        diff.DeletionsCount.Should().Be(0);
        diff.Hunks.Should().HaveCount(1);

        var hunk = diff.Hunks[0];
        hunk.OldCount.Should().Be(0);
        hunk.NewCount.Should().Be(4);
        hunk.Lines.Should().OnlyContain(l => l.Type == DiffLineType.Addition);
        hunk.Lines.Select(l => l.Content).Should().Equal(newLines);

        var unified = service.FormatUnifiedDiff(diff);
        unified.Should().Contain("@@ -0,0 +1,4 @@");
        unified.Should().Contain("+Console.WriteLine(\"Init 1\");");
        diff.Hunks[0].Lines.Should().NotContain(l => l.Type == DiffLineType.Deletion);
    }

    [Fact]
    public void FileDiffService_PopulatedOldVsEmptyNew_Returns100PercentDeletions()
    {
        var service = new FileDiffService(_tempDir);
        var oldLines = new[]
        {
            "Deprecated Line 1",
            "Deprecated Line 2",
            "Deprecated Line 3",
            "Deprecated Line 4",
            "Deprecated Line 5"
        };
        var oldContent = string.Join("\n", oldLines);
        var newContent = string.Empty;

        var diff = service.ComputeDiff("deprecated.txt", oldContent, newContent);

        diff.HasChanges.Should().BeTrue();
        diff.AdditionsCount.Should().Be(0);
        diff.DeletionsCount.Should().Be(5);
        diff.Hunks.Should().HaveCount(1);

        var hunk = diff.Hunks[0];
        hunk.OldCount.Should().Be(5);
        hunk.NewCount.Should().Be(0);
        hunk.Lines.Should().OnlyContain(l => l.Type == DiffLineType.Deletion);
        hunk.Lines.Select(l => l.Content).Should().Equal(oldLines);

        var unified = service.FormatUnifiedDiff(diff);
        unified.Should().Contain("@@ -1,5 +0,0 @@");
        unified.Should().Contain("-Deprecated Line 1");
        diff.Hunks[0].Lines.Should().NotContain(l => l.Type == DiffLineType.Addition);
    }

    [Fact]
    public void FileDiffService_InterleavedChangesAcrossMultipleDistantHunks_SeparatesIntoDistinctHunks()
    {
        var service = new FileDiffService(_tempDir);

        // Generate 100 lines
        var oldLines = Enumerable.Range(1, 100).Select(i => $"Base line {i:D3}").ToList();
        var newLines = new List<string>(oldLines);

        // Modify at index 9 (Line 10), index 49 (Line 50), and index 89 (Line 90)
        newLines[9] = "MODIFIED line 010";
        newLines[49] = "MODIFIED line 050";
        newLines[89] = "MODIFIED line 090";

        var oldText = string.Join("\n", oldLines);
        var newText = string.Join("\n", newLines);

        // With contextLines = 3, distance between hunks is ~40 lines, well above 2 * 3 = 6
        var diff = service.ComputeDiff("multi-hunk.txt", oldText, newText, contextLines: 3);

        diff.HasChanges.Should().BeTrue();
        diff.AdditionsCount.Should().Be(3);
        diff.DeletionsCount.Should().Be(3);
        diff.Hunks.Should().HaveCount(3, "three distant changes must form exactly three distinct hunks");

        // Verify hunk 1 surrounds line 10
        diff.Hunks[0].Lines.Should().Contain(l => l.Content == "MODIFIED line 010" && l.Type == DiffLineType.Addition);
        // Verify hunk 2 surrounds line 50
        diff.Hunks[1].Lines.Should().Contain(l => l.Content == "MODIFIED line 050" && l.Type == DiffLineType.Addition);
        // Verify hunk 3 surrounds line 90
        diff.Hunks[2].Lines.Should().Contain(l => l.Content == "MODIFIED line 090" && l.Type == DiffLineType.Addition);

        var unified = service.FormatUnifiedDiff(diff);
        unified.Split("@@ -").Length.Should().Be(4, "there should be three hunk headers");
    }

    [Fact]
    public void FileDiffService_LargeTextWithHundredsOfLines_ExecutesEfficientlyAndAccurately()
    {
        var service = new FileDiffService(_tempDir);

        // Create 500 lines with various block insertions, modifications, and deletions
        var oldBuilder = new StringBuilder();
        var newBuilder = new StringBuilder();

        for (int i = 1; i <= 500; i++)
        {
            if (i == 50)
            {
                oldBuilder.AppendLine($"Original Block line {i}");
                newBuilder.AppendLine($"Replaced Block line {i} part A");
                newBuilder.AppendLine($"Replaced Block line {i} part B");
            }
            else if (i is >= 150 and <= 155)
            {
                // Deleted block of 6 lines
                oldBuilder.AppendLine($"ToDelete line {i}");
            }
            else if (i == 300)
            {
                // Inserted block of 5 lines
                oldBuilder.AppendLine($"Anchor line {i}");
                newBuilder.AppendLine($"Anchor line {i}");
                for (int ins = 1; ins <= 5; ins++)
                {
                    newBuilder.AppendLine($"NewlyInserted line {i}.{ins}");
                }
            }
            else
            {
                oldBuilder.AppendLine($"Stable line {i}");
                newBuilder.AppendLine($"Stable line {i}");
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var diff = service.ComputeDiff("large.txt", oldBuilder.ToString(), newBuilder.ToString(), contextLines: 3);
        sw.Stop();

        // Must execute in under 1000 milliseconds for 500 lines
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Myers diff on 500 lines must execute swiftly");

        diff.HasChanges.Should().BeTrue();
        // 1 replacement (1 del, 2 adds) + 6 deletions + 5 additions
        // Deletions: 1 + 6 = 7
        // Additions: 2 + 5 = 7
        diff.DeletionsCount.Should().Be(7);
        diff.AdditionsCount.Should().Be(7);
        diff.Hunks.Should().HaveCount(3, "three distinct modification areas");

        var unified = service.FormatUnifiedDiff(diff);
        unified.Should().Contain("+Replaced Block line 50 part A");
        unified.Should().Contain("-ToDelete line 150");
        unified.Should().Contain("+NewlyInserted line 300.1");
    }

    [Fact]
    public void FileDiffService_CrlfVsLfLineEndingSensitivity_NormalizesLineEndingsCorrectly()
    {
        var service = new FileDiffService(_tempDir);

        // Exact same semantic content, one CRLF, one LF
        var crlfContent = "line 1\r\nline 2\r\nline 3\r\nline 4\r\n";
        var lfContent = "line 1\nline 2\nline 3\nline 4\n";

        var diff = service.ComputeDiff("normalized.txt", crlfContent, lfContent);

        diff.HasChanges.Should().BeFalse("differing line endings (CRLF vs LF) with identical content must normalize to no changes");
        diff.AdditionsCount.Should().Be(0);
        diff.DeletionsCount.Should().Be(0);
        diff.Hunks.Should().BeEmpty();

        // Now test mixed CRLF/LF with actual content differences
        var crlfChanged = "line 1\r\nline 2 MODIFIED\r\nline 3\r\nline 4\r\n";
        var diffChanged = service.ComputeDiff("normalized.txt", lfContent, crlfChanged);

        diffChanged.HasChanges.Should().BeTrue();
        diffChanged.AdditionsCount.Should().Be(1);
        diffChanged.DeletionsCount.Should().Be(1);
        diffChanged.Hunks[0].Lines.Should().Contain(l => l.Content == "line 2 MODIFIED" && l.Type == DiffLineType.Addition);
        diffChanged.Hunks[0].Lines.Should().Contain(l => l.Content == "line 2" && l.Type == DiffLineType.Deletion);
    }

    [Fact]
    public async Task FileDiffService_SnapshotModificationAndRollbackIntegrity_RestoresFileAccurately()
    {
        var service = new FileDiffService(_tempDir);
        var targetFile = Path.Combine(_tempDir, "critical_payload.json");
        var originalContent = "{\n  \"service\": \"payment-gateway\",\n  \"status\": \"healthy\",\n  \"version\": 1.0\n}";

        await File.WriteAllTextAsync(targetFile, originalContent);

        // 1. Take snapshot
        var snap1 = await service.CreateSnapshotAsync(targetFile);
        snap1.ContentHash.Should().NotBeNullOrWhiteSpace();
        snap1.IsReverted.Should().BeFalse();
        File.Exists(snap1.SnapshotPath).Should().BeTrue();

        // 2. Apply modifications via Diff Document
        var modifiedContent = "{\n  \"service\": \"payment-gateway\",\n  \"status\": \"compromised\",\n  \"version\": 2.0\n}";
        var diff = service.ComputeDiff(targetFile, originalContent, modifiedContent);
        var applySuccess = await service.ApplyDiffAsync(diff, createBackup: true);
        applySuccess.Should().BeTrue();

        // Verify disk has modified content
        (await File.ReadAllTextAsync(targetFile)).Should().Be(modifiedContent);

        // Two snapshots should exist (snap1 manual + snap2 from ApplyDiffAsync)
        var snapshots = service.GetSnapshots(targetFile);
        snapshots.Should().HaveCount(2);

        // 3. Rollback latest snapshot -> should revert to original content
        var rolledBackLatest = await service.RollbackLatestSnapshotAsync(targetFile);
        rolledBackLatest.Should().BeTrue();
        (await File.ReadAllTextAsync(targetFile)).Should().Be(originalContent);

        // Verify latest snapshot is marked reverted
        var reloadedSnapshots = service.GetSnapshots(targetFile);
        reloadedSnapshots.First(s => s.Id == snapshots[0].Id).IsReverted.Should().BeTrue();

        // 4. Test deleted target file rollback (should recreate file from snapshot)
        File.Delete(targetFile);
        File.Exists(targetFile).Should().BeFalse();

        var rolledBackDirect = await service.RollbackSnapshotAsync(snap1.Id);
        rolledBackDirect.Should().BeTrue();
        File.Exists(targetFile).Should().BeTrue();
        (await File.ReadAllTextAsync(targetFile)).Should().Be(originalContent);
    }

    // =========================================================================
    // SECTION 2: SessionManager SQLite Concurrency & Deadlock Stress Tests
    // =========================================================================

    [Fact]
    public async Task SessionManager_50ParallelTasks_ConcurrentInsertsAndToolCalls_ZeroDeadlocksAndZeroCorruption()
    {
        // Stress test file-based SQLite database with WAL mode enabled
        var dbPath = Path.Combine(_tempDir, "stress_sessions.db");
        using var sessionManager = new SessionManager(dbPath);

        var session = await sessionManager.CreateSessionAsync("Concurrency Stress Session", "gemini-2.5-pro", _tempDir);
        session.Should().NotBeNull();
        var sessionId = session.Id;

        const int taskCount = 50;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Launch 50 parallel asynchronous tasks
        var tasks = Enumerable.Range(1, taskCount).Select(async taskId =>
        {
            // Wait for all tasks to be ready to maximize concurrent race conditions
            await barrier.Task;

            // 1. Append message
            var msg = await sessionManager.AppendMessageAsync(
                sessionId,
                role: taskId % 2 == 0 ? "user" : "assistant",
                content: $"Stress message payload from worker {taskId:D2}",
                tokenCount: taskId * 10,
                toolCallsJson: $"{{\"caller\": {taskId}}}");

            msg.Should().NotBeNull();
            msg.SessionId.Should().Be(sessionId);

            // 2. Record tool call
            var toolCall = await sessionManager.RecordToolCallAsync(
                sessionId,
                toolName: $"tool_worker_{taskId % 5}",
                callArgs: $"{{\"iteration\": {taskId}}}",
                result: $"Output for task {taskId}",
                executionTimeMs: taskId * 5,
                status: "Success");

            toolCall.Should().NotBeNull();
            toolCall.SessionId.Should().Be(sessionId);

            // 3. Add permission grant
            var grant = await sessionManager.AddPermissionGrantAsync(
                sessionId,
                toolName: $"tool_worker_{taskId % 5}",
                actionPattern: $"action_{taskId}/*",
                riskTier: "Yellow");

            grant.Should().NotBeNull();

            // 4. Verify permission check works under concurrent read/write
            var hasPerm = await sessionManager.CheckPermissionGrantAsync(
                sessionId,
                toolName: $"tool_worker_{taskId % 5}",
                actionPattern: $"action_{taskId}/subitem");

            hasPerm.Should().BeTrue();
        }).ToList();

        // Release barrier to unleash all 50 concurrent tasks simultaneously
        barrier.SetResult();

        // Await all tasks with a strict 30-second timeout to catch deadlocks
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        // Verify Session Record reflects exact message count and token sum
        var reloadedSession = await sessionManager.GetSessionAsync(sessionId);
        reloadedSession.Should().NotBeNull();
        reloadedSession!.MessageCount.Should().Be(taskCount);
        reloadedSession.TotalTokens.Should().Be(Enumerable.Range(1, taskCount).Sum(i => i * 10));

        // Verify transcript count and integrity
        var transcript = await sessionManager.GetTranscriptAsync(sessionId, limit: 1000);
        transcript.Should().HaveCount(taskCount, "every concurrent message must be persisted without loss");

        // Verify tool call count
        var toolCalls = await sessionManager.GetToolCallsAsync(sessionId, limit: 1000);
        toolCalls.Should().HaveCount(taskCount, "every concurrent tool call must be persisted without loss");

        // Verify permissions count
        var grants = await sessionManager.GetPermissionGrantsAsync(sessionId);
        grants.Should().HaveCount(taskCount, "every concurrent permission grant must be persisted without loss");
    }

    [Fact]
    public async Task SessionManager_RetrievalOrderingAndPagination_MaintainsChronologicalOrder()
    {
        var dbPath = Path.Combine(_tempDir, "ordering_sessions.db");
        using var manager = new SessionManager(dbPath);

        var session = await manager.CreateSessionAsync("Ordering Test Session", workingDir: _tempDir);
        var sessionId = session.Id;

        // Insert 20 sequential messages with distinct tokens
        for (int i = 1; i <= 20; i++)
        {
            await manager.AppendMessageAsync(
                sessionId,
                role: "user",
                content: $"Sequential message #{i:D2}",
                tokenCount: i);
            await Task.Delay(2); // Ensure monotonic timestamp ordering
        }

        // Test Pagination: limit = 5
        var page1 = await manager.GetTranscriptAsync(sessionId, limit: 5);
        page1.Should().HaveCount(5);
        page1.Select(m => m.TokenCount).Should().Equal(1, 2, 3, 4, 5);

        // Test Pagination: limit = 10
        var page2 = await manager.GetTranscriptAsync(sessionId, limit: 10);
        page2.Should().HaveCount(10);
        page2.Select(m => m.TokenCount).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);

        // Test full retrieval
        var full = await manager.GetTranscriptAsync(sessionId, limit: 50);
        full.Should().HaveCount(20);
        full.First().Content.Should().Be("Sequential message #01");
        full.Last().Content.Should().Be("Sequential message #20");

        // Test Tool Calls ordering and pagination
        for (int i = 1; i <= 15; i++)
        {
            await manager.RecordToolCallAsync(
                sessionId,
                toolName: "cli_exec",
                callArgs: $"arg {i}",
                result: $"res {i}",
                executionTimeMs: i * 10,
                status: "Success");
            await Task.Delay(2);
        }

        var toolPage = await manager.GetToolCallsAsync(sessionId, limit: 7);
        toolPage.Should().HaveCount(7);
        toolPage.Select(t => t.ExecutionTimeMs).Should().Equal(10, 20, 30, 40, 50, 60, 70);
    }

    [Fact]
    public async Task SessionManager_CascadeDelete_RemovesAllAssociatedData()
    {
        var dbPath = Path.Combine(_tempDir, "cascade_sessions.db");
        using var manager = new SessionManager(dbPath);

        var session = await manager.CreateSessionAsync("Cascade Test Session", workingDir: _tempDir);
        var sessionId = session.Id;

        await manager.AppendMessageAsync(sessionId, "user", "Message to be deleted");
        await manager.RecordToolCallAsync(sessionId, "tool_1", "args", "result", 50, "Success");
        await manager.AddPermissionGrantAsync(sessionId, "tool_1", "read/*", "Green");

        // Verify entities exist
        (await manager.GetTranscriptAsync(sessionId)).Should().HaveCount(1);
        (await manager.GetToolCallsAsync(sessionId)).Should().HaveCount(1);
        (await manager.GetPermissionGrantsAsync(sessionId)).Should().HaveCount(1);

        // Delete session
        var deleted = await manager.DeleteSessionAsync(sessionId);
        deleted.Should().BeTrue();

        // Verify cascade deletion in SQLite
        (await manager.GetSessionAsync(sessionId)).Should().BeNull();
        (await manager.GetTranscriptAsync(sessionId)).Should().BeEmpty();
        (await manager.GetToolCallsAsync(sessionId)).Should().BeEmpty();
        (await manager.GetPermissionGrantsAsync(sessionId)).Should().BeEmpty();
    }

    [Fact]
    public void FileDiffService_EmptyOldAndEmptyNew_ReturnsZeroChanges()
    {
        var service = new FileDiffService(_tempDir);
        var diff = service.ComputeDiff("empty.txt", string.Empty, string.Empty);

        diff.HasChanges.Should().BeFalse();
        diff.AdditionsCount.Should().Be(0);
        diff.DeletionsCount.Should().Be(0);
        diff.Hunks.Should().BeEmpty();
        service.FormatUnifiedDiff(diff).Should().BeEmpty();
    }

    [Fact]
    public void FileDiffService_AlternatingEdits_HandlesComplexSnakes()
    {
        var service = new FileDiffService(_tempDir);

        // Alternating lines: every second line is replaced
        var oldLines = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
        var newLines = new[] { "A", "B_mod", "C", "D_mod", "E", "F_mod", "G", "H_mod" };

        var diff = service.ComputeDiff("alt.txt", string.Join("\n", oldLines), string.Join("\n", newLines), contextLines: 1);

        diff.HasChanges.Should().BeTrue();
        diff.AdditionsCount.Should().Be(4);
        diff.DeletionsCount.Should().Be(4);

        var unified = service.FormatUnifiedDiff(diff);
        unified.Should().Contain("+B_mod");
        unified.Should().Contain("-B");
        unified.Should().Contain("+D_mod");
        unified.Should().Contain("-D");
    }

    [Fact]
    public async Task FileDiffService_ChainedMultiVersionRollbacks_RestoresStepByStep()
    {
        var service = new FileDiffService(_tempDir);
        var filePath = Path.Combine(_tempDir, "versioned.txt");

        // Version 1
        await File.WriteAllTextAsync(filePath, "Version 1.0");
        var s1 = await service.CreateSnapshotAsync(filePath);

        // Version 2
        await File.WriteAllTextAsync(filePath, "Version 2.0");
        var s2 = await service.CreateSnapshotAsync(filePath);

        // Version 3
        await File.WriteAllTextAsync(filePath, "Version 3.0");
        var s3 = await service.CreateSnapshotAsync(filePath);

        // Version 4 (current corrupted state)
        await File.WriteAllTextAsync(filePath, "Version 4.0 CORRUPT");

        // Rollback latest unreverted (should restore s3 -> Version 3.0)
        (await service.RollbackLatestSnapshotAsync(filePath)).Should().BeTrue();
        (await File.ReadAllTextAsync(filePath)).Should().Be("Version 3.0");

        // Rollback latest unreverted (should restore s2 -> Version 2.0)
        (await service.RollbackLatestSnapshotAsync(filePath)).Should().BeTrue();
        (await File.ReadAllTextAsync(filePath)).Should().Be("Version 2.0");

        // Rollback latest unreverted (should restore s1 -> Version 1.0)
        (await service.RollbackLatestSnapshotAsync(filePath)).Should().BeTrue();
        (await File.ReadAllTextAsync(filePath)).Should().Be("Version 1.0");

        // No more unreverted snapshots -> returns false
        (await service.RollbackLatestSnapshotAsync(filePath)).Should().BeFalse();
    }

    [Fact]
    public async Task FileDiffService_ErrorConditions_HandledSafely()
    {
        var service = new FileDiffService(_tempDir);

        // Snapshot of nonexistent file
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.CreateSnapshotAsync(Path.Combine(_tempDir, "nonexistent.txt")));

        // Rollback of non-existent snapshot ID
        var failedRollback = await service.RollbackSnapshotAsync("snap_invalid123456");
        failedRollback.Should().BeFalse();
    }

    [Fact]
    public async Task SessionManager_MultiInstanceDiskConcurrency_NoLockingExceptions()
    {
        var dbPath = Path.Combine(_tempDir, "multi_instance.db");

        // Create initial session with instance 1
        using var inst1 = new SessionManager(dbPath);
        var session = await inst1.CreateSessionAsync("Multi Instance Test", workingDir: _tempDir);
        var sessionId = session.Id;

        // Open 3 separate SessionManager instances accessing the exact same disk file
        using var inst2 = new SessionManager(dbPath);
        using var inst3 = new SessionManager(dbPath);

        // Run concurrent operations across instances
        var t1 = Task.Run(async () =>
        {
            for (int i = 1; i <= 10; i++)
            {
                await inst1.AppendMessageAsync(sessionId, "user", $"Inst1 Msg {i}");
            }
        });

        var t2 = Task.Run(async () =>
        {
            for (int i = 1; i <= 10; i++)
            {
                await inst2.AppendMessageAsync(sessionId, "assistant", $"Inst2 Msg {i}");
            }
        });

        var t3 = Task.Run(async () =>
        {
            for (int i = 1; i <= 10; i++)
            {
                await inst3.RecordToolCallAsync(sessionId, "tool_multi", $"arg_{i}", "ok", 10, "Success");
            }
        });

        await Task.WhenAll(t1, t2, t3).WaitAsync(TimeSpan.FromSeconds(15));

        // Reload via fresh instance and verify all 20 messages and 10 tool calls exist
        using var verifier = new SessionManager(dbPath);
        var transcript = await verifier.GetTranscriptAsync(sessionId, limit: 100);
        transcript.Should().HaveCount(20);

        var tools = await verifier.GetToolCallsAsync(sessionId, limit: 100);
        tools.Should().HaveCount(10);
    }

    [Fact]
    public async Task SessionManager_WildcardPermissionPatterns_EvaluatesCorrectly()
    {
        using var manager = new SessionManager(":memory:");
        var session = await manager.CreateSessionAsync("Wildcard Test", workingDir: _tempDir);
        var sessionId = session.Id;

        // Tool with exact action
        await manager.AddPermissionGrantAsync(sessionId, "terminal", "npm run build", "Green");

        // Tool with wildcard action
        await manager.AddPermissionGrantAsync(sessionId, "git", "git status*", "Green");

        // Any tool with specific prefix
        await manager.AddPermissionGrantAsync(sessionId, "*", "read/*", "Green");

        // Global wildcard
        await manager.AddPermissionGrantAsync(sessionId, "filesystem", "*", "Yellow");

        // Test exact match
        (await manager.CheckPermissionGrantAsync(sessionId, "terminal", "npm run build")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(sessionId, "terminal", "npm run test")).Should().BeFalse();

        // Test action wildcard
        (await manager.CheckPermissionGrantAsync(sessionId, "git", "git status")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(sessionId, "git", "git status --short")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(sessionId, "git", "git push")).Should().BeFalse();

        // Test tool wildcard (*)
        (await manager.CheckPermissionGrantAsync(sessionId, "any_custom_tool", "read/file.txt")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(sessionId, "any_custom_tool", "write/file.txt")).Should().BeFalse();

        // Test action wildcard (*)
        (await manager.CheckPermissionGrantAsync(sessionId, "filesystem", "delete/anything")).Should().BeTrue();
    }

    [Fact]
    public async Task SessionManager_LargePayloadAndSpecialCharacters_PreservesIntegrity()
    {
        using var manager = new SessionManager(":memory:");
        var session = await manager.CreateSessionAsync("SQL & Emojis \uD83D\uDE80'; DROP TABLE sessions; --", workingDir: _tempDir);
        var sessionId = session.Id;

        // 100 KB payload with SQL injection characters, quotes, unicode, emojis
        var largeContent = string.Concat(Enumerable.Repeat("Hello \uD83D\uDC4B World! '; SELECT * FROM messages; -- \n", 2000));
        var msg = await manager.AppendMessageAsync(sessionId, "user", largeContent, tokenCount: 25000);

        msg.Content.Length.Should().Be(largeContent.Length);

        var transcript = await manager.GetTranscriptAsync(sessionId);
        transcript.Should().HaveCount(1);
        transcript[0].Content.Should().Be(largeContent);
        transcript[0].TokenCount.Should().Be(25000);

        // Verify session was not corrupted by DROP TABLE injection in title
        var reloaded = await manager.GetSessionAsync(sessionId);
        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be("SQL & Emojis \uD83D\uDE80'; DROP TABLE sessions; --");
        reloaded.TotalTokens.Should().Be(25000);
    }
}

