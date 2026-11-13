using FluentAssertions;
using HUMofA.Agent;
using HUMofA.Diff;
using HUMofA.Sessions;
using HUMofA.SubAgents;
using Xunit;

namespace HUMofA.Tests;

public class SessionAndAgentManagerTests : IDisposable
{
    private readonly string _tempDir;

    public SessionAndAgentManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "humofa-core-tests-" + Guid.NewGuid().ToString("N"));
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
            // Ignore temporary directory locked cleanup
        }
    }

    // ==========================================
    // 1. Session Persistence Engine Tests
    // ==========================================

    [Fact]
    public async Task SessionManager_CreateSession_GeneratesUniqueSessionAndSetsActive()
    {
        using var manager = new SessionManager(":memory:");

        var session1 = await manager.CreateSessionAsync("First Session", "gemini-2.5-pro", _tempDir);
        session1.Should().NotBeNull();
        session1.Id.Should().StartWith("sess_");
        session1.Title.Should().Be("First Session");
        session1.Model.Should().Be("gemini-2.5-pro");
        session1.IsActive.Should().BeTrue("first session should be automatically active");

        var session2 = await manager.CreateSessionAsync("Second Session", "gpt-4o", _tempDir);
        session2.Should().NotBeNull();
        session2.IsActive.Should().BeFalse("subsequent sessions should not be active by default");

        var active = await manager.GetActiveSessionAsync();
        active.Should().NotBeNull();
        active!.Id.Should().Be(session1.Id);
    }

    [Fact]
    public async Task SessionManager_GetAndListSessions_ReturnsPersistedDataInOrder()
    {
        using var manager = new SessionManager(":memory:");

        var s1 = await manager.CreateSessionAsync("Alpha Session", "gemini-2.5-flash", _tempDir, pinned: false);
        await Task.Delay(20);
        var s2 = await manager.CreateSessionAsync("Beta Session (Pinned)", "claude-3-7-sonnet", _tempDir, pinned: true);

        var retrieved = await manager.GetSessionAsync(s1.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Title.Should().Be("Alpha Session");

        var list = await manager.ListSessionsAsync();
        list.Should().HaveCount(2);
        list[0].Id.Should().Be(s2.Id, "pinned sessions should be ordered first");
        list[1].Id.Should().Be(s1.Id);
    }

    [Fact]
    public async Task SessionManager_SetActiveSession_UpdatesActiveStateAtomically()
    {
        using var manager = new SessionManager(":memory:");

        var s1 = await manager.CreateSessionAsync("Session 1", workingDir: _tempDir);
        var s2 = await manager.CreateSessionAsync("Session 2", workingDir: _tempDir);

        (await manager.SetActiveSessionAsync(s2.Id)).Should().BeTrue();

        var active = await manager.GetActiveSessionAsync();
        active.Should().NotBeNull();
        active!.Id.Should().Be(s2.Id);

        var s1Reloaded = await manager.GetSessionAsync(s1.Id);
        s1Reloaded!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task SessionManager_UpdateSession_ModifiesPropertiesCorrectly()
    {
        using var manager = new SessionManager(":memory:");

        var session = await manager.CreateSessionAsync("Initial Title", "gemini-2.5-flash", _tempDir, maxTokens: 4096);

        var updated = await manager.UpdateSessionAsync(
            session.Id,
            title: "Updated Title",
            model: "nous/hermes-3-llama-3.1-405b",
            maxTokens: 16384,
            pinned: true);

        updated.Should().BeTrue();

        var reloaded = await manager.GetSessionAsync(session.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be("Updated Title");
        reloaded.Model.Should().Be("nous/hermes-3-llama-3.1-405b");
        reloaded.MaxTokens.Should().Be(16384);
        reloaded.Pinned.Should().BeTrue();
    }

    [Fact]
    public async Task SessionManager_AppendAndGetTranscript_PreservesOrderAndComputesTokenTotals()
    {
        using var manager = new SessionManager(":memory:");

        var session = await manager.CreateSessionAsync("Chat Session", workingDir: _tempDir);

        var msg1 = await manager.AppendMessageAsync(session.Id, "user", "Hello assistant", tokenCount: 4);
        var msg2 = await manager.AppendMessageAsync(session.Id, "assistant", "Hello user, I can run tools.", tokenCount: 10, toolCallsJson: "[\"tool_1\"]");

        msg1.Should().NotBeNull();
        msg1.Role.Should().Be("user");

        var transcript = await manager.GetTranscriptAsync(session.Id);
        transcript.Should().HaveCount(2);
        transcript[0].Content.Should().Be("Hello assistant");
        transcript[1].Content.Should().Be("Hello user, I can run tools.");
        transcript[1].ToolCallsJson.Should().Be("[\"tool_1\"]");

        // Verify session aggregated tokens & count
        var reloadedSession = await manager.GetSessionAsync(session.Id);
        reloadedSession!.MessageCount.Should().Be(2);
        reloadedSession.TotalTokens.Should().Be(14);
    }

    [Fact]
    public async Task SessionManager_RecordAndGetToolCalls_StoresExecutionTelemetry()
    {
        using var manager = new SessionManager(":memory:");

        var session = await manager.CreateSessionAsync("Tool Session", workingDir: _tempDir);

        var tc = await manager.RecordToolCallAsync(
            sessionId: session.Id,
            toolName: "Grep",
            callArgs: "{\"pattern\":\"SmartApproval\",\"path\":\"src/\"}",
            result: "Found 12 matches",
            executionTimeMs: 42,
            status: "Success");

        tc.Should().NotBeNull();
        tc.Id.Should().StartWith("tc_");
        tc.ExecutionTimeMs.Should().Be(42);

        var toolCalls = await manager.GetToolCallsAsync(session.Id);
        toolCalls.Should().HaveCount(1);
        toolCalls[0].ToolName.Should().Be("Grep");
        toolCalls[0].Result.Should().Be("Found 12 matches");
        toolCalls[0].Status.Should().Be("Success");
    }

    [Fact]
    public async Task SessionManager_PermissionGrants_ExactAndPatternMatching()
    {
        using var manager = new SessionManager(":memory:");

        var session = await manager.CreateSessionAsync("Perm Session", workingDir: _tempDir);

        // Grant 1: Terminal with wildcard action
        await manager.AddPermissionGrantAsync(session.Id, "Terminal", "git *", "Yellow");
        // Grant 2: Specific tool and exact pattern
        await manager.AddPermissionGrantAsync(session.Id, "WriteFile", "src/Config.json", "Red");
        // Grant 3: All tools with glob
        await manager.AddPermissionGrantAsync(session.Id, "*", "temp/*", "Green");

        // Verification
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "git status")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "git diff HEAD")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "rm -rf /")).Should().BeFalse();

        (await manager.CheckPermissionGrantAsync(session.Id, "WriteFile", "src/Config.json")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "WriteFile", "src/Other.json")).Should().BeFalse();

        (await manager.CheckPermissionGrantAsync(session.Id, "DeleteFile", "temp/scratch.txt")).Should().BeTrue();

        var grants = await manager.GetPermissionGrantsAsync(session.Id);
        grants.Should().HaveCount(3);

        // Revoke one grant
        (await manager.RevokePermissionGrantAsync(grants[0].Id)).Should().BeTrue();
        (await manager.GetPermissionGrantsAsync(session.Id)).Should().HaveCount(2);

        // Clear all grants
        (await manager.ClearPermissionGrantsAsync(session.Id)).Should().Be(2);
        (await manager.GetPermissionGrantsAsync(session.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task SessionManager_DeleteSession_CascadesDeleteToMessagesAndToolCalls()
    {
        using var manager = new SessionManager(":memory:");

        var session = await manager.CreateSessionAsync("To Delete", workingDir: _tempDir);
        await manager.AppendMessageAsync(session.Id, "user", "Message in doomed session");
        await manager.RecordToolCallAsync(session.Id, "Bash", "ls", "ok", 10, "Success");
        await manager.AddPermissionGrantAsync(session.Id, "Bash", "*", "Green");

        (await manager.DeleteSessionAsync(session.Id)).Should().BeTrue();

        (await manager.GetSessionAsync(session.Id)).Should().BeNull();
        (await manager.GetTranscriptAsync(session.Id)).Should().BeEmpty();
        (await manager.GetToolCallsAsync(session.Id)).Should().BeEmpty();
        (await manager.GetPermissionGrantsAsync(session.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task SessionManager_FileBasedPersistence_PersistsAcrossInstances()
    {
        var dbPath = Path.Combine(_tempDir, "persisted_sessions.db");

        string sessionId;
        using (var mgr1 = new SessionManager(dbPath))
        {
            var s = await mgr1.CreateSessionAsync("Persistent Session", "gemini-2.5-pro", _tempDir);
            sessionId = s.Id;
            await mgr1.AppendMessageAsync(sessionId, "user", "Saved to disk WAL");
            await mgr1.RecordToolCallAsync(sessionId, "FileWrite", "path.txt", "wrote 10 bytes", 5, "Success");
        }

        // Open fresh instance on the same file
        using (var mgr2 = new SessionManager(dbPath))
        {
            var s = await mgr2.GetSessionAsync(sessionId);
            s.Should().NotBeNull();
            s!.Title.Should().Be("Persistent Session");
            s.MessageCount.Should().Be(1);

            var transcript = await mgr2.GetTranscriptAsync(sessionId);
            transcript.Should().HaveCount(1);
            transcript[0].Content.Should().Be("Saved to disk WAL");

            var tools = await mgr2.GetToolCallsAsync(sessionId);
            tools.Should().HaveCount(1);
            tools[0].ToolName.Should().Be("FileWrite");
        }
    }

    // ==========================================
    // 2. Shared Agent Manager Tests
    // ==========================================

    [Fact]
    public void AgentManager_InitializesDefaultAgents_AndListsThem()
    {
        var manager = new AgentManager();
        var agents = manager.ListAgents();

        agents.Should().HaveCountGreaterOrEqualTo(3);
        agents.Select(a => a.Id).Should().Contain(["default-agent", "code-reviewer", "bug-fixer"]);

        var primary = manager.GetAgent("default-agent");
        primary.Should().NotBeNull();
        primary!.ToolPermissions.Should().Be(AgentToolPermissions.All);
        primary.HasPermission(AgentToolPermissions.Terminal).Should().BeTrue();
        primary.HasPermission(AgentToolPermissions.SubAgents).Should().BeTrue();
    }

    [Fact]
    public void AgentManager_RegisterCustomAgent_AndUpdatesConfigAndStatus()
    {
        var manager = new AgentManager();

        var customConfig = new AgentConfig(
            Id: "security-auditor",
            Name: "Security Auditor",
            SystemPrompt: "Audit AST code structures for safety vulnerabilities.",
            PreferredModel: "claude-3-7-sonnet",
            WorkingDirectory: _tempDir,
            ToolPermissions: AgentToolPermissions.FileOps | AgentToolPermissions.Git,
            MaxTokenBudget: 24000,
            AutonomyTier: "Conservative");

        manager.RegisterAgent(customConfig);

        var retrieved = manager.GetAgent("security-auditor");
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Security Auditor");
        retrieved.ToolPermissions.Should().HaveFlag(AgentToolPermissions.FileOps);
        retrieved.ToolPermissions.Should().NotHaveFlag(AgentToolPermissions.Terminal);

        // Update config
        var updatedConfig = customConfig with { Name = "Lead Security Auditor", MaxTokenBudget = 32000 };
        manager.UpdateAgentConfig(updatedConfig).Should().BeTrue();
        manager.GetAgent("security-auditor")!.Name.Should().Be("Lead Security Auditor");

        // Update status lifecycle
        manager.GetAgentStatus("security-auditor").Should().Be(AgentStatus.Idle);
        manager.UpdateAgentStatus("security-auditor", AgentStatus.WaitingForApproval).Should().BeTrue();
        manager.GetAgentStatus("security-auditor").Should().Be(AgentStatus.WaitingForApproval);

        // Delete agent
        manager.DeleteAgent("security-auditor").Should().BeTrue();
        manager.GetAgent("security-auditor").Should().BeNull();
    }

    [Fact]
    public async Task AgentManager_ExecuteTask_DelegatesToSubAgentOrchestratorAndTracksState()
    {
        var manager = new AgentManager();

        var request = new AgentTaskRequest(
            AgentId: "default-agent",
            TaskPrompt: "Verify system readiness");

        var result = await manager.ExecuteTaskAsync(request, async (spec, rpc, ct) =>
        {
            await Task.Yield();
            return $"Task executed successfully for role '{spec.Role}'";
        });

        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Output.Should().Contain("Task executed successfully");
        result.FinalStatus.Should().Be(AgentStatus.Completed);
        result.ContextTranscript.Should().NotBeEmpty();

        var agentState = manager.GetAgentState("default-agent");
        agentState.Should().NotBeNull();
        agentState!.Status.Should().Be(AgentStatus.Completed);
        agentState.TokensConsumed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AgentManager_ExecuteTask_HandlesFailureGracefully()
    {
        var manager = new AgentManager();

        var request = new AgentTaskRequest(
            AgentId: "bug-fixer",
            TaskPrompt: "Failing job");

        var result = await manager.ExecuteTaskAsync(request, (spec, rpc, ct) =>
        {
            throw new InvalidOperationException("Simulation fault in agent logic");
        });

        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.FinalStatus.Should().Be(AgentStatus.Failed);
        result.Output.Should().Contain("Simulation fault in agent logic");

        var state = manager.GetAgentState("bug-fixer");
        state!.Status.Should().Be(AgentStatus.Failed);
        state.LastError.Should().Contain("Simulation fault in agent logic");
    }

    // ==========================================
    // 3. File Diff Engine & Rollback Tests
    // ==========================================

    [Fact]
    public void FileDiffService_ComputeDiff_IdentifiesAdditionsAndDeletions()
    {
        var service = new FileDiffService(_tempDir);

        var oldContent = """
            public class Program
            {
                public static void Main()
                {
                    Console.WriteLine("Hello");
                }
            }
            """;

        var newContent = """
            public class Program
            {
                public static void Main()
                {
                    Console.WriteLine("Hello World");
                    Console.WriteLine("New Line");
                }
            }
            """;

        var diff = service.ComputeDiff("Program.cs", oldContent, newContent, contextLines: 2);

        diff.Should().NotBeNull();
        diff.HasChanges.Should().BeTrue();
        diff.AdditionsCount.Should().Be(2);
        diff.DeletionsCount.Should().Be(1);
        diff.Hunks.Should().HaveCount(1);

        var hunk = diff.Hunks[0];
        hunk.Lines.Should().Contain(l => l.Type == DiffLineType.Deletion && l.Content.Contains("Console.WriteLine(\"Hello\");"));
        hunk.Lines.Should().Contain(l => l.Type == DiffLineType.Addition && l.Content.Contains("Console.WriteLine(\"Hello World\");"));
        hunk.Lines.Should().Contain(l => l.Type == DiffLineType.Addition && l.Content.Contains("Console.WriteLine(\"New Line\");"));
    }

    [Fact]
    public void FileDiffService_ComputeDiff_HandlesIdenticalContent()
    {
        var service = new FileDiffService(_tempDir);
        var content = "line 1\nline 2\nline 3";

        var diff = service.ComputeDiff("test.txt", content, content);

        diff.HasChanges.Should().BeFalse();
        diff.AdditionsCount.Should().Be(0);
        diff.DeletionsCount.Should().Be(0);
        diff.Hunks.Should().BeEmpty();

        var unified = service.FormatUnifiedDiff(diff);
        unified.Should().BeEmpty();
    }

    [Fact]
    public void FileDiffService_FormatUnifiedDiff_ProducesStandardUnifiedDiffFormat()
    {
        var service = new FileDiffService(_tempDir);

        var oldContent = "one\ntwo\nthree\nfour\nfive";
        var newContent = "one\ntwo (modified)\nthree\nfour\nfive\nsix";

        var diff = service.ComputeDiff("sample.txt", oldContent, newContent, contextLines: 1);
        var unified = service.FormatUnifiedDiff(diff);

        unified.Should().StartWith("--- a/sample.txt");
        unified.Should().Contain("+++ b/sample.txt");
        unified.Should().Contain("@@ -");
        unified.Should().Contain("-two");
        unified.Should().Contain("+two (modified)");
        unified.Should().Contain("+six");
    }

    [Fact]
    public async Task FileDiffService_SnapshotAndRollback_RestoresOriginalContent()
    {
        var backupDir = Path.Combine(_tempDir, "backups");
        var service = new FileDiffService(backupDir);

        var testFile = Path.Combine(_tempDir, "config.json");
        var originalContent = "{\"env\": \"production\", \"version\": 1}";
        await File.WriteAllTextAsync(testFile, originalContent);

        // 1. Create snapshot
        var snapshot = await service.CreateSnapshotAsync(testFile);
        snapshot.Should().NotBeNull();
        snapshot.FilePath.Should().Be(Path.GetFullPath(testFile));
        File.Exists(snapshot.SnapshotPath).Should().BeTrue();

        // 2. Modify target file
        await File.WriteAllTextAsync(testFile, "{\"env\": \"corrupted\", \"version\": 99}");
        (await File.ReadAllTextAsync(testFile)).Should().Contain("corrupted");

        // 3. Rollback snapshot
        var rolledBack = await service.RollbackSnapshotAsync(snapshot.Id);
        rolledBack.Should().BeTrue();

        var restoredContent = await File.ReadAllTextAsync(testFile);
        restoredContent.Should().Be(originalContent, "rollback should restore exact original content");

        // 4. Verify snapshot listing
        var history = service.GetSnapshots(testFile);
        history.Should().HaveCount(1);
        history[0].IsReverted.Should().BeTrue();
    }

    [Fact]
    public async Task FileDiffService_ApplyDiff_CreatesBackupAndAppliesNewContent()
    {
        var backupDir = Path.Combine(_tempDir, "backups");
        var service = new FileDiffService(backupDir);

        var targetFile = Path.Combine(_tempDir, "source.txt");
        await File.WriteAllTextAsync(targetFile, "initial content");

        var diff = service.ComputeDiff(targetFile, "initial content", "new updated content");

        var applied = await service.ApplyDiffAsync(diff, createBackup: true);
        applied.Should().BeTrue();

        var contentOnDisk = await File.ReadAllTextAsync(targetFile);
        contentOnDisk.Should().Be("new updated content");

        // Rollback latest
        var reverted = await service.RollbackLatestSnapshotAsync(targetFile);
        reverted.Should().BeTrue();
        (await File.ReadAllTextAsync(targetFile)).Should().Be("initial content");
    }
}
