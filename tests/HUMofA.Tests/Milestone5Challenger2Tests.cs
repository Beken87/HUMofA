using System.Text;
using FluentAssertions;
using HUMofA.Agent;
using HUMofA.Backends;
using HUMofA.Cli;
using HUMofA.Diff;
using HUMofA.Gateway;
using HUMofA.Memory;
using HUMofA.Sessions;
using HUMofA.Skills;
using HUMofA.SubAgents;
using Xunit;

namespace HUMofA.Tests;

/// <summary>
/// Empirical challenge test suite for challenger_m5_2:
/// Covers CLI session lifecycle, diff confirmations, SmartApproval governance,
/// and command autocomplete edge cases.
/// </summary>
public sealed class Milestone5Challenger2Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly MockTerminalConsole _mockConsole;
    private readonly SessionManager _sessionManager;
    private readonly AgentManager _agentManager;
    private readonly FileDiffService _diffService;
    private readonly SmartApproval _smartApproval;
    private readonly UnifiedMemoryStore _memoryStore;
    private readonly SkillManager _skillManager;
    private readonly MultiModelRouter _router;
    private readonly ExecutionEnvironmentManager _backends;
    private readonly SubAgentOrchestrator _subAgents;
    private readonly CentralEventBus _eventBus;
    private readonly GhostTypingSync _ghostTyping;

    public Milestone5Challenger2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "humofa-challenger2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _mockConsole = new MockTerminalConsole();
        _sessionManager = new SessionManager(Path.Combine(_tempDir, "sessions.db"));
        _subAgents = new SubAgentOrchestrator();
        _agentManager = new AgentManager(_subAgents);
        _diffService = new FileDiffService(Path.Combine(_tempDir, "backups"));
        _eventBus = new CentralEventBus();
        _smartApproval = new SmartApproval(_eventBus);
        _memoryStore = new UnifiedMemoryStore(Path.Combine(_tempDir, "memory"));
        _skillManager = new SkillManager(Path.Combine(_tempDir, "skills"), searchIndex: _memoryStore.SearchIndex);
        _router = new MultiModelRouter();
        _backends = new ExecutionEnvironmentManager();
        _ghostTyping = new GhostTypingSync();
    }

    public void Dispose()
    {
        _sessionManager.Dispose();
        _memoryStore.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp directory cleanup lock
        }
    }

    private InteractiveCli CreateCli(
        string? initialSessionId = null,
        string? initialModel = null,
        ITerminalConsole? customConsole = null)
    {
        return new InteractiveCli(
            customConsole ?? _mockConsole,
            _sessionManager,
            _agentManager,
            _diffService,
            _smartApproval,
            _memoryStore,
            _skillManager,
            _router,
            _backends,
            _subAgents,
            _eventBus,
            _ghostTyping,
            initialSessionId: initialSessionId,
            initialModel: initialModel,
            initialWorkingDir: _tempDir);
    }

    // =========================================================================
    // SECTION 1: Session Lifecycle & Transcript Continuity
    // =========================================================================

    [Fact]
    public async Task SessionLifecycle_CreateAppendResume_VerifiesTranscriptContinuityAndTokenTracking()
    {
        // Step 1: Launch first CLI session, create message, then exit
        var cli1 = CreateCli();
        _mockConsole.QueueInput("First prompt in session");
        _mockConsole.QueueInput("/exit");

        await cli1.RunAsync();

        cli1.ActiveSession.Should().NotBeNull();
        string sessionId = cli1.ActiveSession!.Id;
        sessionId.Should().NotBeNullOrWhiteSpace();

        var initialTranscript = await _sessionManager.GetTranscriptAsync(sessionId);
        initialTranscript.Should().HaveCount(2); // 1 user + 1 assistant
        initialTranscript[0].Role.Should().Be("user");
        initialTranscript[0].Content.Should().Be("First prompt in session");
        initialTranscript[1].Role.Should().Be("assistant");

        var sessionRecord1 = await _sessionManager.GetSessionAsync(sessionId);
        sessionRecord1.Should().NotBeNull();
        sessionRecord1!.MessageCount.Should().Be(2);
        sessionRecord1.TotalTokens.Should().BeGreaterThan(0);

        // Step 2: Resume session in a brand new CLI instance via initialSessionId (simulating --session <id>)
        _mockConsole.ClearOutput();
        var cli2 = CreateCli(initialSessionId: sessionId);
        _mockConsole.QueueInput("Second prompt in resumed session");
        _mockConsole.QueueInput("/exit");

        await cli2.RunAsync();

        cli2.ActiveSession.Should().NotBeNull();
        cli2.ActiveSession!.Id.Should().Be(sessionId);

        // Step 3: Verify transcript continuity across executions
        var fullTranscript = await _sessionManager.GetTranscriptAsync(sessionId);
        fullTranscript.Should().HaveCount(4);
        fullTranscript[0].Role.Should().Be("user");
        fullTranscript[0].Content.Should().Be("First prompt in session");
        fullTranscript[1].Role.Should().Be("assistant");
        fullTranscript[2].Role.Should().Be("user");
        fullTranscript[2].Content.Should().Be("Second prompt in resumed session");
        fullTranscript[3].Role.Should().Be("assistant");

        var finalSession = await _sessionManager.GetSessionAsync(sessionId);
        finalSession!.MessageCount.Should().Be(4);
        finalSession.TotalTokens.Should().BeGreaterThan(sessionRecord1.TotalTokens);
    }

    [Fact]
    public async Task SessionLifecycle_ResumeNonExistentSessionId_FallsBackGracefullyWithoutCrashing()
    {
        string fakeSessionId = "sess_non_existent_" + Guid.NewGuid().ToString("N");

        var cli = CreateCli(initialSessionId: fakeSessionId);
        _mockConsole.QueueInput("/exit");

        var act = async () => await cli.RunAsync();
        await act.Should().NotThrowAsync();

        cli.ActiveSession.Should().NotBeNull();
        cli.ActiveSession!.Id.Should().NotBe(fakeSessionId);
        cli.ActiveSession.Title.Should().Be("CLI Session");
    }

    [Fact]
    public async Task SessionLifecycle_ModelOverrideViaCliArguments_PreservesOrOverridesCorrectly()
    {
        // Create session with model "claude-3-7-sonnet-latest"
        var session = await _sessionManager.CreateSessionAsync("Sonnet Session", "claude-3-7-sonnet-latest", _tempDir);

        // Case A: Resume without specifying model -> restores session model
        var cliDefault = CreateCli(initialSessionId: session.Id);
        await cliDefault.InitializeSessionAsync();
        cliDefault.ActiveModel.Should().Be("claude-3-7-sonnet-latest");

        // Case B: Resume with explicit model override -> uses override
        var cliOverride = CreateCli(initialSessionId: session.Id, initialModel: "gpt-4o");
        await cliOverride.InitializeSessionAsync();
        cliOverride.ActiveModel.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task SessionLifecycle_MultiTurnExchangesAcrossResumptions_PreservesExactTranscriptOrderAndContent()
    {
        var session = await _sessionManager.CreateSessionAsync("MultiTurn Session", "gemini-2.5-flash", _tempDir);

        for (int turn = 1; turn <= 3; turn++)
        {
            var cli = CreateCli(initialSessionId: session.Id);
            _mockConsole.QueueInput($"Turn {turn} question");
            _mockConsole.QueueInput("/exit");
            await cli.RunAsync();
        }

        var transcript = await _sessionManager.GetTranscriptAsync(session.Id);
        transcript.Should().HaveCount(6);

        transcript[0].Content.Should().Be("Turn 1 question");
        transcript[2].Content.Should().Be("Turn 2 question");
        transcript[4].Content.Should().Be("Turn 3 question");

        for (int i = 0; i < 6; i++)
        {
            string expectedRole = i % 2 == 0 ? "user" : "assistant";
            transcript[i].Role.Should().Be(expectedRole);
        }
    }

    [Fact]
    public async Task ProgramCli_RunInteractiveCliLoopAsync_WithSessionArgument_ResumesSessionDeterministically()
    {
        var appCore = new AppCore(_tempDir);
        await appCore.InitializeAsync(isHeadless: true);

        var createdSession = await appCore.SessionManager.CreateSessionAsync("Core Session", "gpt-4o", _tempDir);
        await appCore.SessionManager.AppendMessageAsync(createdSession.Id, "user", "Core message pre-CLI");

        var localConsole = new MockTerminalConsole();
        localConsole.QueueInput("New message via AppCore CLI");
        localConsole.QueueInput("/exit");

        await Program.RunInteractiveCliLoopAsync(
            appCore,
            console: localConsole,
            sessionId: createdSession.Id,
            workDir: _tempDir);

        var transcript = await appCore.SessionManager.GetTranscriptAsync(createdSession.Id);
        transcript.Should().HaveCount(3); // 1 pre + 1 user + 1 assistant
        transcript[0].Content.Should().Be("Core message pre-CLI");
        transcript[1].Content.Should().Be("New message via AppCore CLI");
    }

    // =========================================================================
    // SECTION 2: Diff Preview Prompts (y, n, d, a)
    // =========================================================================

    [Fact]
    public async Task DiffPrompt_SimulatingYes_AppliesChangesAndCreatesBackup()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string targetFile = Path.Combine(_tempDir, "file_yes.txt");
        await File.WriteAllTextAsync(targetFile, "int a = 1;\nint b = 2;\n");

        _mockConsole.QueueChoice("y");

        bool result = await cli.PromptFileModificationAsync(targetFile, "int a = 1;\nint b = 99;\n");
        result.Should().BeTrue();

        string current = await File.ReadAllTextAsync(targetFile);
        current.Should().Be("int a = 1;\nint b = 99;\n");

        var snapshots = _diffService.GetSnapshots(targetFile);
        snapshots.Should().NotBeEmpty();

        _mockConsole.RenderedDiffs.Should().ContainSingle();
        _mockConsole.StrippedOutputText.Should().Contain($"Successfully applied changes to {targetFile}");
    }

    [Fact]
    public async Task DiffPrompt_SimulatingNo_LeavesFileUntouchedAndReturnsFalse()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string targetFile = Path.Combine(_tempDir, "file_no.txt");
        await File.WriteAllTextAsync(targetFile, "immutable content\n");

        _mockConsole.QueueChoice("n");

        bool result = await cli.PromptFileModificationAsync(targetFile, "mutated content\n");
        result.Should().BeFalse();

        string current = await File.ReadAllTextAsync(targetFile);
        current.Should().Be("immutable content\n");

        var snapshots = _diffService.GetSnapshots(targetFile);
        snapshots.Should().BeEmpty();

        _mockConsole.StrippedOutputText.Should().Contain("rejected by user");
    }

    [Fact]
    public async Task DiffPrompt_SimulatingDetailsThenYes_RendersFullProposedContentThenApplies()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string targetFile = Path.Combine(_tempDir, "file_details_yes.txt");
        await File.WriteAllTextAsync(targetFile, "old version\n");

        string proposed = "detailed new version line 1\ndetailed new version line 2\n";

        // First prompt choice: 'd' (details), Second prompt choice: 'y' (approve)
        _mockConsole.QueueChoice("d");
        _mockConsole.QueueChoice("y");

        bool result = await cli.PromptFileModificationAsync(targetFile, proposed);
        result.Should().BeTrue();

        string current = await File.ReadAllTextAsync(targetFile);
        current.Should().Be(proposed);

        // Verify that the proposed content was written to console during details inspection
        var rawText = _mockConsole.RawOutputText;
        rawText.Should().Contain("detailed new version line 1");
        rawText.Should().Contain($"Successfully applied changes to {targetFile}");

        // The diff was rendered at least twice (initial + after details)
        _mockConsole.RenderedDiffs.Count.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task DiffPrompt_SimulatingDetailsThenNo_RendersFullProposedContentThenRejects()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string targetFile = Path.Combine(_tempDir, "file_details_no.txt");
        await File.WriteAllTextAsync(targetFile, "original stay untouched\n");

        string proposed = "should never be saved line\n";

        // First prompt choice: 'd' (details), Second prompt choice: 'n' (reject)
        _mockConsole.QueueChoice("d");
        _mockConsole.QueueChoice("n");

        bool result = await cli.PromptFileModificationAsync(targetFile, proposed);
        result.Should().BeFalse();

        string current = await File.ReadAllTextAsync(targetFile);
        current.Should().Be("original stay untouched\n");

        var rawText = _mockConsole.RawOutputText;
        rawText.Should().Contain("should never be saved line");
        _mockConsole.StrippedOutputText.Should().Contain("rejected by user");
    }

    [Fact]
    public async Task DiffPrompt_NewFileCreation_PromptsDiffAndAppliesCorrectly()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string targetFullPath = Path.Combine(_tempDir, "new_created_file.cs");
        File.Exists(targetFullPath).Should().BeFalse();

        _mockConsole.QueueChoice("y");

        bool result = await cli.PromptFileModificationAsync(targetFullPath, "public class NewFile {}\n");
        result.Should().BeTrue();

        File.Exists(targetFullPath).Should().BeTrue();
        string content = await File.ReadAllTextAsync(targetFullPath);
        content.Should().Be("public class NewFile {}\n");
    }

    [Fact]
    public async Task DiffPrompt_IdenticalContent_DetectsNoChangesAndBypassesPrompt()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string targetFile = Path.Combine(_tempDir, "same.txt");
        await File.WriteAllTextAsync(targetFile, "same text content\n");

        // No choices queued - should not need any input
        bool result = await cli.PromptFileModificationAsync(targetFile, "same text content\n");
        result.Should().BeTrue();

        _mockConsole.StrippedOutputText.Should().Contain($"No changes detected for '{targetFile}'.");
        _mockConsole.RenderedDiffs.Should().BeEmpty();
    }

    [Fact]
    public async Task DiffPrompt_SimulatingAlways_GrantsSessionScopeAndAutoApprovesSubsequentChanges()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string targetFile = Path.Combine(_tempDir, "always_file.txt");
        await File.WriteAllTextAsync(targetFile, "v1\n");

        // Choice 'a' grants session permission
        _mockConsole.QueueChoice("a");

        bool result1 = await cli.PromptFileModificationAsync(targetFile, "v2\n");
        result1.Should().BeTrue();

        // Second modification to same file requires NO prompt input
        bool result2 = await cli.PromptFileModificationAsync(targetFile, "v3\n");
        result2.Should().BeTrue();

        string current = await File.ReadAllTextAsync(targetFile);
        current.Should().Be("v3\n");

        _mockConsole.StrippedOutputText.Should().Contain($"Pre-approved via session grant: applying changes to '{targetFile}'");
    }

    [Fact]
    public async Task DiffPrompt_CaseInsensitiveInput_HandlesUpperAndFullWords()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string file1 = Path.Combine(_tempDir, "case_yes.txt");
        await File.WriteAllTextAsync(file1, "old 1\n");
        _mockConsole.QueueChoice("YES");
        bool res1 = await cli.PromptFileModificationAsync(file1, "new 1\n");
        res1.Should().BeTrue();

        string file2 = Path.Combine(_tempDir, "case_no.txt");
        await File.WriteAllTextAsync(file2, "old 2\n");
        _mockConsole.QueueChoice("NO");
        bool res2 = await cli.PromptFileModificationAsync(file2, "new 2\n");
        res2.Should().BeFalse();
    }

    [Fact]
    public async Task DiffPrompt_RelativePath_WritesToSessionWorkingDirectory()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        // When a relative path "rel_file.txt" is passed to PromptFileModificationAsync,
        // InteractiveCli resolves fullPath = _workingDirectory / "rel_file.txt" and passes it to ComputeDiff/ApplyDiffAsync.
        // File is correctly written to _tempDir (session working directory), NOT CurrentDirectory!
        string relPath = "rel_file_" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        _mockConsole.QueueChoice("y");

        bool result = await cli.PromptFileModificationAsync(relPath, "content in rel_file\n");
        result.Should().BeTrue();

        string sessionTarget = Path.Combine(cli.WorkingDirectory, relPath);
        File.Exists(sessionTarget).Should().BeTrue();

        string cwdTarget = Path.GetFullPath(relPath);
        if (!string.Equals(cwdTarget, sessionTarget, StringComparison.OrdinalIgnoreCase))
        {
            File.Exists(cwdTarget).Should().BeFalse();
        }

        // Cleanup
        try { File.Delete(sessionTarget); } catch { }
    }

    // =========================================================================
    // SECTION 3: SmartApproval Countdown and Prompt Resolution
    // =========================================================================

    [Fact]
    public async Task SmartApproval_YellowTier_ExplicitYes_ApprovesImmediately()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueConfirmation(true);

        bool approved = await cli.EvaluateSmartApprovalAsync("git_commit", RiskTier.Yellow, "Commit staged changes");
        approved.Should().BeTrue();

        _mockConsole.StrippedOutputText.Should().Contain("Yellow Tier");
    }

    [Fact]
    public async Task SmartApproval_YellowTier_ExplicitNo_RejectsImmediately()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueConfirmation(false);

        bool approved = await cli.EvaluateSmartApprovalAsync("git_commit", RiskTier.Yellow, "Commit staged changes");
        approved.Should().BeFalse();
    }

    [Fact]
    public async Task SmartApproval_YellowTier_GracePeriodCountdownElapsed_AutoApprovesWhenOperationCanceled()
    {
        // Custom console where ConfirmAsync actually blocks until canceled to simulate countdown expiration
        var countdownConsole = new CountdownSimulatingMockTerminalConsole();

        var cli = CreateCli(customConsole: countdownConsole);
        await cli.InitializeSessionAsync();

        // Provide a 20ms cancellation token
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        bool approved = await cli.EvaluateSmartApprovalAsync("git_push", RiskTier.Yellow, "Push branch to remote", cts.Token);
        approved.Should().BeTrue();

        countdownConsole.StrippedOutputText.Should().Contain("Grace period elapsed: auto-approved (Yellow Tier)");
    }

    [Fact]
    public async Task SmartApproval_YellowTier_NonInteractiveConsole_AutoApprovesImmediately()
    {
        var nonInteractiveConsole = new NonInteractiveMockTerminalConsole();

        var cli = CreateCli(customConsole: nonInteractiveConsole);
        await cli.InitializeSessionAsync();

        bool approved = await cli.EvaluateSmartApprovalAsync("git_pull", RiskTier.Yellow, "Fetch remote changes");
        approved.Should().BeTrue();
    }

    [Fact]
    public async Task SmartApproval_RedTier_SimulatingYes_ApprovesSingleAction()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueChoice("y");

        bool approved = await cli.EvaluateSmartApprovalAsync("format_drive", RiskTier.Red, "Format volume D:");
        approved.Should().BeTrue();

        _mockConsole.StrippedOutputText.Should().Contain("RED TIER PERMISSION REQUIRED");
    }

    [Fact]
    public async Task SmartApproval_RedTier_SimulatingNo_DeniesAction()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueChoice("n");

        bool approved = await cli.EvaluateSmartApprovalAsync("drop_database", RiskTier.Red, "Drop primary database");
        approved.Should().BeFalse();

        _mockConsole.StrippedOutputText.Should().Contain("Action denied by user.");
    }

    [Fact]
    public async Task SmartApproval_RedTier_SimulatingDetailsThenYes_RendersDetailsAndApproves()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueChoice("d");
        _mockConsole.QueueChoice("y");

        bool approved = await cli.EvaluateSmartApprovalAsync("modify_registry", RiskTier.Red, "Write HKLM key");
        approved.Should().BeTrue();

        _mockConsole.StrippedOutputText.Should().Contain("Action details: modify_registry | Write HKLM key");
    }

    [Fact]
    public async Task SmartApproval_RedTier_SimulatingDetailsThenNo_RendersDetailsAndDenies()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueChoice("d");
        _mockConsole.QueueChoice("n");

        bool approved = await cli.EvaluateSmartApprovalAsync("kill_system_process", RiskTier.Red, "Terminate critical pid");
        approved.Should().BeFalse();

        _mockConsole.StrippedOutputText.Should().Contain("Action details: kill_system_process | Terminate critical pid");
        _mockConsole.StrippedOutputText.Should().Contain("Action denied by user.");
    }

    [Fact]
    public async Task SmartApproval_RedTier_SimulatingAlways_GrantsSessionWhitelistAndBypassesFuturePrompts()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueChoice("a");

        bool firstApproved = await cli.EvaluateSmartApprovalAsync("wipe_temp_directory", RiskTier.Red, "Delete all temp files");
        firstApproved.Should().BeTrue();

        _mockConsole.StrippedOutputText.Should().Contain("Granted session-scoped approval for wipe_temp_directory");

        // Second invocation: pre-approved via session grant without requiring input
        bool secondApproved = await cli.EvaluateSmartApprovalAsync("wipe_temp_directory", RiskTier.Red, "Delete all temp files");
        secondApproved.Should().BeTrue();

        _mockConsole.StrippedOutputText.Should().Contain("wipe_temp_directory: pre-approved via session grant");
    }

    [Fact]
    public async Task SmartApproval_RedTier_InvalidChoice_DefaultsToNoAndDenies()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueChoice("unexpected_garbage_input");

        bool approved = await cli.EvaluateSmartApprovalAsync("rm_rf", RiskTier.Red, "Delete everything");
        approved.Should().BeFalse();

        _mockConsole.StrippedOutputText.Should().Contain("Action denied by user.");
    }

    // =========================================================================
    // SECTION 4: Autocomplete Edge Cases
    // =========================================================================

    [Theory]
    [InlineData("/m", "/model")]
    [InlineData("/mod", "/model")]
    [InlineData("/a", "/agent")]
    [InlineData("/sk", "/skill")]
    [InlineData("/ses", "/sessions")]
    [InlineData("/di", "/diff")]
    [InlineData("/cl", "/clear")]
    [InlineData("/co", "/compact")]
    [InlineData("/mem", "/memory")]
    [InlineData("/k", "/kanban")]
    [InlineData("/e", "/exec")]
    [InlineData("/exi", "/exit")]
    [InlineData("/p", "/permissions")]
    [InlineData("/h", "/help")]
    [InlineData("/q", "/quit")]
    public void Autocomplete_ValidPrefixMatch_ResolvesCorrectSlashCommand(string prefix, string expectedMatch)
    {
        var suggestions = InteractiveCli.SlashCommands;
        var match = suggestions.FirstOrDefault(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        match.Should().NotBeNull();
        match.Should().Be(expectedMatch);
    }

    [Theory]
    [InlineData("/MODEL", "/model")]
    [InlineData("/Agent", "/agent")]
    [InlineData("/SKILL", "/skill")]
    [InlineData("/cOMPACT", "/compact")]
    [InlineData("/KANBAN", "/kanban")]
    public void Autocomplete_CaseInsensitivePrefixMatch_ResolvesCorrectCommand(string upperPrefix, string expectedMatch)
    {
        var suggestions = InteractiveCli.SlashCommands;
        var match = suggestions.FirstOrDefault(s => s.StartsWith(upperPrefix, StringComparison.OrdinalIgnoreCase));

        match.Should().NotBeNull();
        match.Should().Be(expectedMatch);
    }

    [Fact]
    public void Autocomplete_AmbiguousPrefix_ResolvesFirstAlphabeticalOrOrderedMatch()
    {
        var suggestions = InteractiveCli.SlashCommands;

        // "/s" is shared by "/skill" and "/sessions". First in array order is "/skill".
        var match = suggestions.FirstOrDefault(s => s.StartsWith("/s", StringComparison.OrdinalIgnoreCase));
        match.Should().Be("/skill");

        // "/e" is shared by "/exec" and "/exit". First in array order is "/exec".
        var matchE = suggestions.FirstOrDefault(s => s.StartsWith("/e", StringComparison.OrdinalIgnoreCase));
        matchE.Should().Be("/exec");
    }

    [Fact]
    public void Autocomplete_EmptyQuery_ResolvesFirstSuggestion()
    {
        var suggestions = InteractiveCli.SlashCommands;
        var match = suggestions.FirstOrDefault(s => s.StartsWith(string.Empty, StringComparison.OrdinalIgnoreCase));

        // When query is empty, startsWith("") is true for every element, returning the first registered command
        match.Should().NotBeNull();
        match.Should().Be(suggestions[0]);
    }

    [Theory]
    [InlineData("/unknown")]
    [InlineData("/xyz")]
    [InlineData("/foobar")]
    [InlineData("/model_extra_suffix")]
    [InlineData("/123")]
    [InlineData("random_text")]
    public void Autocomplete_UnknownPrefix_ReturnsNullWithoutThrowing(string unknownPrefix)
    {
        var suggestions = InteractiveCli.SlashCommands;
        var match = suggestions.FirstOrDefault(s => s.StartsWith(unknownPrefix, StringComparison.OrdinalIgnoreCase));

        match.Should().BeNull();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Autocomplete_WhitespaceQuery_ReturnsNull(string whitespaceQuery)
    {
        var suggestions = InteractiveCli.SlashCommands;
        var match = suggestions.FirstOrDefault(s => s.StartsWith(whitespaceQuery, StringComparison.OrdinalIgnoreCase));

        match.Should().BeNull();
    }

    [Theory]
    [InlineData("model")]
    [InlineData("agent")]
    [InlineData("skill")]
    [InlineData("help")]
    [InlineData("exit")]
    public void Autocomplete_MissingSlashPrefix_ReturnsNull(string prefixWithoutSlash)
    {
        var suggestions = InteractiveCli.SlashCommands;
        var match = suggestions.FirstOrDefault(s => s.StartsWith(prefixWithoutSlash, StringComparison.OrdinalIgnoreCase));

        // Commands require leading slash
        match.Should().BeNull();
    }

    [Fact]
    public void Autocomplete_EmptySuggestionsList_ReturnsNullSafely()
    {
        IReadOnlyList<string> emptySuggestions = Array.Empty<string>();
        var match = emptySuggestions.FirstOrDefault(s => s.StartsWith("/m", StringComparison.OrdinalIgnoreCase));

        match.Should().BeNull();
    }

    [Fact]
    public void Autocomplete_SlashCommandsCompleteness_AllInteractiveCommandsRegistered()
    {
        var expected = new[]
        {
            "/model",
            "/agent",
            "/skill",
            "/sessions",
            "/diff",
            "/clear",
            "/compact",
            "/memory",
            "/kanban",
            "/exec",
            "/permissions",
            "/help",
            "/exit",
            "/quit",
            "/subagent",
            "/subagents",
            "/btw",
            "/note",
            "/retry",
            "/continue",
            "/undo",
            "/status",
            "/cost",
            "/think",
            "/doctor"
        };

        InteractiveCli.SlashCommands.Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// Mock console where ConfirmAsync awaits CancellationToken to simulate grace period timeout.
    /// </summary>
    private sealed class CountdownSimulatingMockTerminalConsole : ITerminalConsole
    {
        private readonly StringBuilder _stripped = new();
        public int WindowWidth => 80;
        public int WindowHeight => 24;
        public bool IsInteractive => true;
        public bool IsAnsiSupported => false;
        public string StrippedOutputText => _stripped.ToString();

        public void Write(string text, ConsoleColor? color = null) => _stripped.Append(text);
        public void WriteLine(string? text = null, ConsoleColor? color = null) => _stripped.AppendLine(text ?? "");
        public void WriteAnsi(string ansiSequence) { }
        public void ClearLine() { }
        public void MoveCursorUp(int lines = 1) { }
        public void ClearScreen() { }
        public void RenderMarkup(string markup) => _stripped.Append(AnsiTerminal.StripMarkup(markup));
        public void RenderMarkupLine(string markup) => _stripped.AppendLine(AnsiTerminal.StripMarkup(markup));
        public void RenderRule(string title = "", char lineChar = '─') => _stripped.AppendLine(title);
        public void RenderCard(ToolExecutionCard card) { }
        public void RenderDiff(DiffDocument diff) { }
        public void RenderStatusBadges(PromptBadgeContext context) { }

        public Task<string?> ReadLineAsync(PromptOptions? options = null, CancellationToken ct = default)
            => Task.FromResult<string?>("/exit");

        public async Task<bool> ConfirmAsync(string prompt, bool defaultYes = false, CancellationToken ct = default)
        {
            RenderMarkupLine($"[bold yellow]{prompt}[/] [dim]{(defaultYes ? "[Y/n]" : "[y/N]")}[/]");
            // Await cancellation to simulate the user letting the grace period countdown expire
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return defaultYes;
        }

        public Task<string> PromptChoiceAsync(string prompt, IReadOnlyList<string> options, string defaultOption, CancellationToken ct = default)
            => Task.FromResult(defaultOption);
    }

    /// <summary>
    /// Helper mock console where IsInteractive is false for testing non-interactive fallbacks.
    /// </summary>
    private sealed class NonInteractiveMockTerminalConsole : ITerminalConsole
    {
        public int WindowWidth => 80;
        public int WindowHeight => 24;
        public bool IsInteractive => false;
        public bool IsAnsiSupported => false;

        public void Write(string text, ConsoleColor? color = null) { }
        public void WriteLine(string? text = null, ConsoleColor? color = null) { }
        public void WriteAnsi(string ansiSequence) { }
        public void ClearLine() { }
        public void MoveCursorUp(int lines = 1) { }
        public void ClearScreen() { }
        public void RenderMarkup(string markup) { }
        public void RenderMarkupLine(string markup) { }
        public void RenderRule(string title = "", char lineChar = '─') { }
        public void RenderCard(ToolExecutionCard card) { }
        public void RenderDiff(DiffDocument diff) { }
        public void RenderStatusBadges(PromptBadgeContext context) { }

        public Task<string?> ReadLineAsync(PromptOptions? options = null, CancellationToken ct = default)
            => Task.FromResult<string?>("/exit");

        public Task<bool> ConfirmAsync(string prompt, bool defaultYes = false, CancellationToken ct = default)
            => Task.FromResult(defaultYes);

        public Task<string> PromptChoiceAsync(string prompt, IReadOnlyList<string> options, string defaultOption, CancellationToken ct = default)
            => Task.FromResult(defaultOption);
    }
}
