using System.Collections.Concurrent;
using FluentAssertions;
using HUMofA.Agent;
using HUMofA.Sessions;
using Xunit;

namespace HUMofA.Tests;

/// <summary>
/// Empirical challenge test suite for Milestone 4 (challenger_m4_2).
/// Adversarially stress-tests:
/// 1. Session Permission wildcard matching, exact matching, risk tiers, and revocation.
/// 2. AgentManager lifecycle across 100 distinct agents, bitmasks, status transitions, duplicate IDs.
/// 3. Physical SQLite file persistence (.humofa-data/test_sessions.db) across instance disposals and reloads.
/// </summary>
public class Milestone4Challenger2Tests : IDisposable
{
    private readonly string _testDbPath;
    private readonly string _testDataDir;

    public Milestone4Challenger2Tests()
    {
        _testDataDir = Path.Combine(Directory.GetCurrentDirectory(), ".humofa-data");
        Directory.CreateDirectory(_testDataDir);
        _testDbPath = Path.Combine(_testDataDir, $"test_sessions_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        CleanDbFiles(_testDbPath);
    }

    private static void CleanDbFiles(string dbPath)
    {
        try
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            var wal = dbPath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
            var shm = dbPath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    // =========================================================================
    // SECTION 1: PERMISSION WILDCARDS, RISK TIERS & REVOCATION
    // =========================================================================

    [Fact]
    public async Task Permissions_ActionWildcardMatching_MatchesPrefixAndRejectsNonMatching()
    {
        using var manager = new SessionManager(":memory:");
        var session = await manager.CreateSessionAsync("Permission Test Session");

        // Grant: Action pattern with wildcard 'git:*'
        await manager.AddPermissionGrantAsync(session.Id, toolName: "*", actionPattern: "git:*", riskTier: "Yellow");

        // Should match git:commit, git:status, git:push
        (await manager.CheckPermissionGrantAsync(session.Id, "git", "git:commit")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "git:status")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "AnyTool", "git:push")).Should().BeTrue();

        // Must NOT match bash:git or svn:commit or git_commit (without colon)
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "bash:git")).Should().BeFalse();
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "svn:commit")).Should().BeFalse();
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "git_commit")).Should().BeFalse();
    }

    [Fact]
    public async Task Permissions_UniversalWildcard_MatchesAllToolsAndActions()
    {
        using var manager = new SessionManager(":memory:");
        var session = await manager.CreateSessionAsync("Universal Wildcard Session");

        // Grant '*' tool with '*' pattern
        await manager.AddPermissionGrantAsync(session.Id, toolName: "*", actionPattern: "*", riskTier: "Green");

        // Any tool and any action must pass
        (await manager.CheckPermissionGrantAsync(session.Id, "bash", "rm -rf /")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "git commit")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "WebSearch", "https://api.github.com")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "BrowserCdp", "click #login")).Should().BeTrue();
    }

    [Fact]
    public async Task Permissions_ExactToolAndActionMatching_DistinguishesStrictEquivalence()
    {
        using var manager = new SessionManager(":memory:");
        var session = await manager.CreateSessionAsync("Exact Matching Session");

        await manager.AddPermissionGrantAsync(session.Id, toolName: "Terminal", actionPattern: "npm test", riskTier: "Yellow");

        // Exact match (case insensitive)
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "npm test")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "terminal", "npm test")).Should().BeTrue();

        // Different tool -> reject
        (await manager.CheckPermissionGrantAsync(session.Id, "PowerShell", "npm test")).Should().BeFalse();

        // Different action -> reject
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "npm run build")).Should().BeFalse();
        (await manager.CheckPermissionGrantAsync(session.Id, "Terminal", "npm test -- --watch")).Should().BeFalse();
    }

    [Fact]
    public async Task Permissions_RevocationAndClearing_RemovesGrantsInstantly()
    {
        using var manager = new SessionManager(":memory:");
        var session = await manager.CreateSessionAsync("Revocation Session");

        var g1 = await manager.AddPermissionGrantAsync(session.Id, "git", "git:*", "Yellow");
        var g2 = await manager.AddPermissionGrantAsync(session.Id, "fs", "read:*", "Green");

        // Pre-revocation check
        (await manager.CheckPermissionGrantAsync(session.Id, "git", "git:checkout")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "fs", "read:file.txt")).Should().BeTrue();

        // Revoke g1
        var revoked = await manager.RevokePermissionGrantAsync(g1.Id);
        revoked.Should().BeTrue();

        // g1 must now fail, g2 still succeeds
        (await manager.CheckPermissionGrantAsync(session.Id, "git", "git:checkout")).Should().BeFalse();
        (await manager.CheckPermissionGrantAsync(session.Id, "fs", "read:file.txt")).Should().BeTrue();

        // Clear all grants
        var clearedCount = await manager.ClearPermissionGrantsAsync(session.Id);
        clearedCount.Should().Be(1);

        // g2 now fails
        (await manager.CheckPermissionGrantAsync(session.Id, "fs", "read:file.txt")).Should().BeFalse();

        // Non-existent grant revocation returns false
        var nonExistentRevoked = await manager.RevokePermissionGrantAsync("perm_non_existent");
        nonExistentRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task Permissions_ToolNameWildcardBehavior_DocumentExactSemantics()
    {
        using var manager = new SessionManager(":memory:");
        var session = await manager.CreateSessionAsync("ToolName Wildcard Exploration");

        // Case 1: ToolName is "*" -> Matches any tool
        await manager.AddPermissionGrantAsync(session.Id, toolName: "*", actionPattern: "read:*", riskTier: "Green");
        (await manager.CheckPermissionGrantAsync(session.Id, "FileOps", "read:test.txt")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "AnyCustomTool", "read:test.txt")).Should().BeTrue();

        // Case 2: ToolName is exact "git" with actionPattern "commit"
        await manager.AddPermissionGrantAsync(session.Id, toolName: "git", actionPattern: "commit", riskTier: "Yellow");
        (await manager.CheckPermissionGrantAsync(session.Id, "git", "commit")).Should().BeTrue();
        (await manager.CheckPermissionGrantAsync(session.Id, "bash", "commit")).Should().BeFalse();

        // Case 3: Exploring ToolName with prefix pattern like "git:*"
        // In current implementation: CheckPermissionGrantAsync checks grant.ToolName == "*" || grant.ToolName == toolName
        // If a grant has ToolName "git:*", it matches exact "git:*", not regex.
        await manager.AddPermissionGrantAsync(session.Id, toolName: "git:*", actionPattern: "*", riskTier: "Yellow");
        var matchDirect = await manager.CheckPermissionGrantAsync(session.Id, "git:commit", "anything");
        // Document: ToolName matching requires either "*" or exact string match,
        // while ActionPattern supports full glob/regex (*, ?).
        matchDirect.Should().BeFalse("ToolName globbing is handled via '*' or exact tool match; ActionPattern handles fine-grained globbing");
    }

    // =========================================================================
    // SECTION 2: AGENTMANAGER LIFECYCLE, 100 AGENTS & BITMASKS
    // =========================================================================

    [Fact]
    public void AgentManager_Register100Agents_WithDiverseBitmasks_PreservesAllConfigurations()
    {
        var manager = new AgentManager();
        var allPermissions = new[]
        {
            AgentToolPermissions.None,
            AgentToolPermissions.Terminal,
            AgentToolPermissions.FileOps,
            AgentToolPermissions.WebSearch,
            AgentToolPermissions.BrowserCdp,
            AgentToolPermissions.Git,
            AgentToolPermissions.SubAgents,
            AgentToolPermissions.All,
            AgentToolPermissions.Terminal | AgentToolPermissions.FileOps,
            AgentToolPermissions.Git | AgentToolPermissions.Terminal,
            AgentToolPermissions.BrowserCdp | AgentToolPermissions.WebSearch,
            AgentToolPermissions.FileOps | AgentToolPermissions.SubAgents,
            AgentToolPermissions.Terminal | AgentToolPermissions.Git | AgentToolPermissions.FileOps
        };

        for (int i = 0; i < 100; i++)
        {
            var bitmask = allPermissions[i % allPermissions.Length];
            var config = new AgentConfig(
                Id: $"agent_stress_{i:D3}",
                Name: $"Stress Agent {i}",
                SystemPrompt: $"You are agent number {i} configured with permission bitmask {bitmask}.",
                PreferredModel: (i % 2 == 0) ? "gemini-2.5-flash" : "claude-3-7-sonnet",
                WorkingDirectory: $"/workspace/agent_{i}",
                ToolPermissions: bitmask,
                MaxTokenBudget: 10000 + (i * 200),
                AutonomyTier: (i % 3 == 0) ? "Conservative" : (i % 3 == 1) ? "Guarded" : "FullAutonomy"
            );

            var registered = manager.RegisterAgent(config);
            registered.Should().NotBeNull();
            registered.Id.Should().Be($"agent_stress_{i:D3}");
        }

        var allAgents = manager.ListAgents();
        // 100 registered + 3 default = 103 agents
        allAgents.Should().HaveCount(103);

        // Verify state of every single agent
        for (int i = 0; i < 100; i++)
        {
            var expectedBitmask = allPermissions[i % allPermissions.Length];
            var agent = manager.GetAgent($"agent_stress_{i:D3}");
            agent.Should().NotBeNull();
            agent!.ToolPermissions.Should().Be(expectedBitmask);
            agent.MaxTokenBudget.Should().Be(10000 + (i * 200));

            // Verify HasPermission helper accurately computes bitmask logic
            if (expectedBitmask.HasFlag(AgentToolPermissions.Terminal))
            {
                agent.HasPermission(AgentToolPermissions.Terminal).Should().BeTrue();
            }
            if (expectedBitmask == AgentToolPermissions.None)
            {
                agent.HasPermission(AgentToolPermissions.Terminal).Should().BeFalse();
                agent.HasPermission(AgentToolPermissions.FileOps).Should().BeFalse();
            }

            // Verify initial status is Idle
            manager.GetAgentStatus($"agent_stress_{i:D3}").Should().Be(AgentStatus.Idle);
        }
    }

    [Fact]
    public void AgentManager_LifecycleTransitions_IdleToRunningToWaitingToCompleted()
    {
        var manager = new AgentManager();
        var agentId = "lifecycle_agent_01";
        manager.RegisterAgent(new AgentConfig(agentId, "Lifecycle Test Agent", "Testing lifecycle"));

        // Initial state
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.Idle);

        // 1. Idle -> Running
        manager.UpdateAgentStatus(agentId, AgentStatus.Running).Should().BeTrue();
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.Running);
        var stateRunning = manager.GetAgentState(agentId);
        stateRunning!.Status.Should().Be(AgentStatus.Running);
        stateRunning.LastError.Should().BeNull();

        // 2. Running -> WaitingForApproval
        manager.UpdateAgentStatus(agentId, AgentStatus.WaitingForApproval).Should().BeTrue();
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.WaitingForApproval);

        // 3. WaitingForApproval -> Running
        manager.UpdateAgentStatus(agentId, AgentStatus.Running).Should().BeTrue();
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.Running);

        // 4. Running -> Completed
        manager.UpdateAgentStatus(agentId, AgentStatus.Completed).Should().BeTrue();
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.Completed);

        // 5. Test Paused and Failed transitions with error tracking
        manager.UpdateAgentStatus(agentId, AgentStatus.Paused).Should().BeTrue();
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.Paused);

        manager.UpdateAgentStatus(agentId, AgentStatus.Failed, "Resource limit exceeded").Should().BeTrue();
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.Failed);
        var stateFailed = manager.GetAgentState(agentId);
        stateFailed!.LastError.Should().Be("Resource limit exceeded");
    }

    [Fact]
    public void AgentManager_DuplicateIdRegistration_OverwritesAndResetsToIdle()
    {
        var manager = new AgentManager();
        var agentId = "dup_agent_01";

        // Initial registration
        manager.RegisterAgent(new AgentConfig(agentId, "Original Name", "Prompt 1", ToolPermissions: AgentToolPermissions.Terminal));
        manager.UpdateAgentStatus(agentId, AgentStatus.Running);
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.Running);

        // Duplicate registration with same ID
        manager.RegisterAgent(new AgentConfig(agentId, "Overwritten Name", "Prompt 2", ToolPermissions: AgentToolPermissions.Git));

        var updated = manager.GetAgent(agentId);
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Overwritten Name");
        updated.ToolPermissions.Should().Be(AgentToolPermissions.Git);

        // Re-registration resets state to Idle
        manager.GetAgentStatus(agentId).Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public void AgentManager_NonExistentAndInvalidIds_ThrowsOrReturnsFalse()
    {
        var manager = new AgentManager();

        // Get non-existent agent
        manager.GetAgent("non_existent").Should().BeNull();
        manager.GetAgentState("non_existent").Should().BeNull();

        // Get status of non-existent agent throws KeyNotFoundException
        var actStatus = () => manager.GetAgentStatus("non_existent");
        actStatus.Should().Throw<KeyNotFoundException>();

        // Update status of non-existent agent returns false
        manager.UpdateAgentStatus("non_existent", AgentStatus.Running).Should().BeFalse();

        // Delete non-existent agent returns false
        manager.DeleteAgent("non_existent").Should().BeFalse();

        // Empty / whitespace ID validation
        var actEmptyRegister = () => manager.RegisterAgent(new AgentConfig("", "Invalid", "Prompt"));
        actEmptyRegister.Should().Throw<ArgumentException>();

        var actWhitespaceGet = () => manager.GetAgent("   ");
        actWhitespaceGet.Should().Throw<ArgumentException>();

        var actWhitespaceStatus = () => manager.GetAgentStatus("");
        actWhitespaceStatus.Should().Throw<ArgumentException>();
    }

    // =========================================================================
    // SECTION 3: PHYSICAL SQLITE FILE PERSISTENCE (.humofa-data/test_sessions.db)
    // =========================================================================

    [Fact]
    public async Task SessionManager_PhysicalDiskPersistence_Reloads100PercentIdenticallyAcrossDisposes()
    {
        CleanDbFiles(_testDbPath);

        var createdSessionIds = new List<string>();
        var expectedMessages = new Dictionary<string, List<(string role, string content, int tokens, string? toolCalls)>>();
        var expectedToolCalls = new Dictionary<string, List<(string name, string args, string result, long ms, string status)>>();
        var expectedPerms = new Dictionary<string, List<(string tool, string pattern, string tier)>>();

        // =====================================================
        // STEP 1: Write extensive data into disk database instance
        // =====================================================
        using (var mgr1 = new SessionManager(_testDbPath))
        {
            for (int i = 0; i < 5; i++)
            {
                var s = await mgr1.CreateSessionAsync(
                    title: $"Persistent Session {i}",
                    model: (i % 2 == 0) ? "gemini-2.5-pro" : "nous/hermes-3-llama-3.1-405b",
                    workingDir: Path.Combine(_testDataDir, $"workspace_{i}"),
                    autonomyTier: (i == 0) ? "FullAutonomy" : "Guarded",
                    maxTokens: 4096 * (i + 1),
                    pinned: (i % 2 == 1));

                createdSessionIds.Add(s.Id);
                expectedMessages[s.Id] = new();
                expectedToolCalls[s.Id] = new();
                expectedPerms[s.Id] = new();

                // Write messages
                for (int m = 0; m < 4; m++)
                {
                    var role = (m % 2 == 0) ? "user" : "assistant";
                    var content = $"Session {i} Message {m}: Payload data verifying disk storage.";
                    var tokens = 10 * (m + 1);
                    var toolJson = (m == 3) ? "[{\"name\":\"Grep\",\"callId\":\"c1\"}]" : null;

                    await mgr1.AppendMessageAsync(s.Id, role, content, tokens, toolJson);
                    expectedMessages[s.Id].Add((role, content, tokens, toolJson));
                }

                // Write tool calls
                for (int t = 0; t < 3; t++)
                {
                    var toolName = $"Tool_{t}";
                    var args = $"{{\"param\":\"val_{t}\",\"sessionIndex\":{i}}}";
                    var result = $"Tool result for session {i} execution {t}";
                    var ms = 25 * (t + 1);
                    var status = (t == 2) ? "Warning" : "Success";

                    await mgr1.RecordToolCallAsync(s.Id, toolName, args, result, ms, status);
                    expectedToolCalls[s.Id].Add((toolName, args, result, ms, status));
                }

                // Write permission grants
                await mgr1.AddPermissionGrantAsync(s.Id, "*", "git:*", "Yellow");
                expectedPerms[s.Id].Add(("*", "git:*", "Yellow"));

                await mgr1.AddPermissionGrantAsync(s.Id, "Terminal", "npm test", "Green");
                expectedPerms[s.Id].Add(("Terminal", "npm test", "Green"));

                await mgr1.AddPermissionGrantAsync(s.Id, "DeleteDatabase", "prod_db", "Red");
                expectedPerms[s.Id].Add(("DeleteDatabase", "prod_db", "Red"));
            }

            // Verify DB file actually exists on physical disk before disposal
            File.Exists(_testDbPath).Should().BeTrue("physical SQLite database file must exist on disk");
        }
        // mgr1 is now fully disposed, closing keep-alive connection and releasing WAL locks.

        // =====================================================
        // STEP 2: Instantiate completely fresh SessionManager on same DB file
        // =====================================================
        using (var mgr2 = new SessionManager(_testDbPath))
        {
            var sessions = await mgr2.ListSessionsAsync();
            sessions.Should().HaveCount(5, "all 5 sessions must survive disposal and reopen");

            // Verify sessions ordering (pinned first, then updated_at DESC)
            var pinnedSessions = sessions.Where(s => s.Pinned).ToList();
            pinnedSessions.Should().HaveCount(2);

            foreach (var sessionId in createdSessionIds)
            {
                var s = await mgr2.GetSessionAsync(sessionId);
                s.Should().NotBeNull();
                s!.MessageCount.Should().Be(4, "aggregated message count must reload accurately");
                s.TotalTokens.Should().Be(10 + 20 + 30 + 40, "total token aggregation must match exactly 100");

                // Verify transcripts 100% reload
                var transcript = await mgr2.GetTranscriptAsync(sessionId);
                transcript.Should().HaveCount(4);
                var expMsgs = expectedMessages[sessionId];
                for (int m = 0; m < 4; m++)
                {
                    transcript[m].Role.Should().Be(expMsgs[m].role);
                    transcript[m].Content.Should().Be(expMsgs[m].content);
                    transcript[m].TokenCount.Should().Be(expMsgs[m].tokens);
                    transcript[m].ToolCallsJson.Should().Be(expMsgs[m].toolCalls);
                }

                // Verify tool calls 100% reload
                var toolCalls = await mgr2.GetToolCallsAsync(sessionId);
                toolCalls.Should().HaveCount(3);
                var expTools = expectedToolCalls[sessionId];
                for (int t = 0; t < 3; t++)
                {
                    toolCalls[t].ToolName.Should().Be(expTools[t].name);
                    toolCalls[t].CallArgs.Should().Be(expTools[t].args);
                    toolCalls[t].Result.Should().Be(expTools[t].result);
                    toolCalls[t].ExecutionTimeMs.Should().Be(expTools[t].ms);
                    toolCalls[t].Status.Should().Be(expTools[t].status);
                }

                // Verify permission grants 100% reload and function
                var perms = await mgr2.GetPermissionGrantsAsync(sessionId);
                perms.Should().HaveCount(3);

                (await mgr2.CheckPermissionGrantAsync(sessionId, "git", "git:checkout")).Should().BeTrue();
                (await mgr2.CheckPermissionGrantAsync(sessionId, "Terminal", "npm test")).Should().BeTrue();
                (await mgr2.CheckPermissionGrantAsync(sessionId, "DeleteDatabase", "prod_db")).Should().BeTrue();
                (await mgr2.CheckPermissionGrantAsync(sessionId, "DeleteDatabase", "other_db")).Should().BeFalse();
            }

            // Append another message in mgr2
            await mgr2.AppendMessageAsync(createdSessionIds[0], "user", "Message written during second instance lifetime");
        }

        // =====================================================
        // STEP 3: Verify third instance reloads appended data without corruption
        // =====================================================
        using (var mgr3 = new SessionManager(_testDbPath))
        {
            var transcript0 = await mgr3.GetTranscriptAsync(createdSessionIds[0]);
            transcript0.Should().HaveCount(5, "appended message in mgr2 must persist into mgr3");
            transcript0[4].Content.Should().Be("Message written during second instance lifetime");
        }
    }
}
