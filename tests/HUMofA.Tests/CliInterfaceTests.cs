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

public class CliInterfaceTests : IDisposable
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

    public CliInterfaceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "humofa-cli-tests-" + Guid.NewGuid().ToString("N"));
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
            // Ignore temp dir lock cleanup
        }
    }

    private InteractiveCli CreateCli(string? initialSessionId = null, string? initialModel = null)
    {
        return new InteractiveCli(
            _mockConsole,
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

    // ==========================================
    // 1. Terminal Console & VT100 Markup Tests
    // ==========================================

    [Fact]
    public void AnsiTerminal_MarkupParsing_TranslatesTagsToAnsiSequences()
    {
        string markup = "[bold][cyan]Hello[/] [red]World[/]!";
        string ansi = AnsiTerminal.ParseMarkupToAnsi(markup, ansiEnabled: true);

        ansi.Should().Contain("\x1b[1m");  // bold
        ansi.Should().Contain("\x1b[36m"); // cyan
        ansi.Should().Contain("\x1b[31m"); // red
        ansi.Should().Contain("\x1b[0m");  // reset

        string stripped = AnsiTerminal.StripMarkup(markup);
        stripped.Should().Be("Hello World!");

        string strippedAnsi = AnsiTerminal.StripAnsi(ansi);
        strippedAnsi.Should().Be("Hello World!");
    }

    [Fact]
    public void TerminalConsole_RenderRule_RendersHeaderLineWithCorrectWidth()
    {
        _mockConsole.RenderRule("Test Section", '─');

        _mockConsole.OutputLines.Should().NotBeEmpty();
        var ruleLine = _mockConsole.OutputLines.Last();
        ruleLine.Should().Contain("Test Section");
        ruleLine.Should().Contain("─");
    }

    [Fact]
    public void TerminalConsole_RenderStatusBadges_FormatsBadgeHeaders()
    {
        var context = new PromptBadgeContext(
            WorkingDirectory: "C:\\Projects\\App",
            GitBranch: "feature/cli",
            ActiveModel: "gpt-4o",
            SessionId: "sess_1234567890",
            TokenCount: 14200);

        _mockConsole.RenderStatusBadges(context);

        _mockConsole.RenderedBadges.Should().ContainSingle();
        var recorded = _mockConsole.RenderedBadges[0];
        recorded.GitBranch.Should().Be("feature/cli");
        recorded.ActiveModel.Should().Be("gpt-4o");
        recorded.FormatTokens().Should().Be("14.2k tokens");
        recorded.FormatShortSessionId().Should().Be("123456");

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("feature/cli");
        text.Should().Contain("gpt-4o");
        text.Should().Contain("14.2k tokens");
        text.Should().Contain("sess:123456");
    }

    [Fact]
    public void TerminalConsole_RenderCard_RendersBoxedToolCardWithFoldedLines()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"Line {i}").ToList();
        var card = new ToolExecutionCard(
            ToolName: "GrepSearch",
            Parameters: "query='SmartApproval'",
            Output: string.Join("\n", lines),
            ExecutionTimeMs: 42,
            IsSuccess: true,
            IsFolded: true);

        _mockConsole.RenderCard(card);

        _mockConsole.RenderedCards.Should().ContainSingle();
        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("GrepSearch");
        text.Should().Contain("query='SmartApproval'");
        text.Should().Contain("Completed in 42ms");
        text.Should().Contain("hidden");
    }

    [Fact]
    public void TerminalConsole_RenderDiff_HighlightsAdditionsAndDeletions()
    {
        string oldContent = "line 1\nline 2 to delete\nline 3\n";
        string newContent = "line 1\nline 2 added\nline 3\n";

        var diff = _diffService.ComputeDiff("src/File.cs", oldContent, newContent);
        _mockConsole.RenderDiff(diff);

        _mockConsole.RenderedDiffs.Should().ContainSingle();
        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Proposed Change: src/File.cs");
        text.Should().Contain("+1, -1");
        text.Should().Contain("- line 2 to delete");
        text.Should().Contain("+ line 2 added");
    }

    // ==========================================
    // 2. Interactive CLI Slash Commands Tests
    // ==========================================

    [Fact]
    public async Task InteractiveCli_HelpCommand_RendersAllSlashCommands()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/help");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("/model");
        output.Should().Contain("/agent");
        output.Should().Contain("/skill");
        output.Should().Contain("/sessions");
        output.Should().Contain("/diff");
        output.Should().Contain("/memory");
        output.Should().Contain("/kanban");
        output.Should().Contain("/exec");
        output.Should().Contain("/permissions");
        output.Should().Contain("/exit");
    }

    [Fact]
    public async Task InteractiveCli_ModelCommand_ListsAndSwitchesModel()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/model");
        _mockConsole.QueueInput("/model gpt-4o");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        cli.ActiveModel.Should().Be("gpt-4o");
        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Available Built-in Models");
        output.Should().Contain("Switched active model to: gpt-4o");
    }

    [Fact]
    public async Task InteractiveCli_AgentCommand_ListsAndSwitchesAgents()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/agent create analyst \"Data Analyst\"");
        _mockConsole.QueueInput("/agent list");
        _mockConsole.QueueInput("/agent switch analyst");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var agent = _agentManager.GetAgent("analyst");
        agent.Should().NotBeNull();
        agent!.Name.Should().Be("Data Analyst");

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Successfully registered agent: Data Analyst");
        output.Should().Contain("Registered Agents");
        output.Should().Contain("Switched to agent: Data Analyst");
    }

    [Fact]
    public async Task InteractiveCli_SkillCommand_ListsAndRunsSkills()
    {
        string skillMarkdown = @"---
name: code-review
description: Analyze pull requests for bugs and performance issues
provider: custom
---
# Code Review Skill
Execute AST analysis.";

        await _skillManager.InstallSkillFromMarkdownAsync(skillMarkdown);

        var cli = CreateCli();
        _mockConsole.QueueInput("/skill list");
        _mockConsole.QueueInput("/skill run code-review");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Installed Skills");
        output.Should().Contain("code-review");
        output.Should().Contain("Analyze pull requests for bugs");
        output.Should().Contain("Skill: code-review");
    }

    [Fact]
    public async Task InteractiveCli_SessionsCommand_ListsCreatesAndResumes()
    {
        var session1 = await _sessionManager.CreateSessionAsync("Alpha Session", "gemini-2.5-pro", _tempDir);
        await _sessionManager.AppendMessageAsync(session1.Id, "user", "Message in alpha session");

        var cli = CreateCli();
        _mockConsole.QueueInput("/sessions list");
        _mockConsole.QueueInput($"/sessions resume {session1.Id}");
        _mockConsole.QueueInput("/sessions new \"Beta Session\"");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Persistent Sessions");
        output.Should().Contain("Alpha Session");
        output.Should().Contain($"Resumed session: Alpha Session");
        output.Should().Contain("Message in alpha session");
        output.Should().Contain("Created and switched to new session: Beta Session");
    }

    [Fact]
    public async Task InteractiveCli_DiffCommand_RendersRecentSnapshots()
    {
        string testFile = Path.Combine(_tempDir, "example.txt");
        await File.WriteAllTextAsync(testFile, "initial content");
        await _diffService.CreateSnapshotAsync(testFile);

        var cli = CreateCli();
        _mockConsole.QueueInput("/diff");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Recent File Rollback Snapshots");
        output.Should().Contain("example.txt");
    }

    [Fact]
    public async Task InteractiveCli_MemoryCommand_QueriesAndAppendsEntries()
    {
        await _memoryStore.InitializeAsync();

        var cli = CreateCli();
        _mockConsole.QueueInput("/memory add ArchitectureNotes \"We use SQLite WAL mode for concurrency.\"");
        _mockConsole.QueueInput("/memory query SQLite");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Appended entry under ## ArchitectureNotes to MEMORY.md");
        output.Should().Contain("Memory Search: 'SQLite'");
        output.Should().Contain("SQLite WAL mode");
    }

    [Fact]
    public async Task InteractiveCli_KanbanCommand_RendersAsciiBoard()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/kanban");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Sub-Agent Kanban Board");
        output.Should().Contain("TODO");
        output.Should().Contain("IN PROGRESS");
        output.Should().Contain("DONE");
    }

    [Fact]
    public async Task InteractiveCli_PermissionsCommand_ListsAndClearsGrants()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        await _sessionManager.AddPermissionGrantAsync(cli.ActiveSession!.Id, "FileOps", "src/*.cs", "Yellow");

        _mockConsole.QueueInput("/permissions");
        _mockConsole.QueueInput("/permissions clear");
        _mockConsole.QueueInput("/permissions");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Active Session Permission Grants");
        output.Should().Contain("Tool: FileOps - Pattern: src/*.cs");
        output.Should().Contain("Cleared 1 session-scoped permission grants");
        output.Should().Contain("No persistent session-scoped permission grants active");
    }

    // ==========================================
    // 3. Natural Language & Markdown Tokenizer Tests
    // ==========================================

    [Fact]
    public async Task InteractiveCli_NaturalLanguagePrompt_StreamsTokensAndPersistsTranscript()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("Can you explain how HUMofA works?");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        cli.ActiveSession.Should().NotBeNull();
        var transcript = await _sessionManager.GetTranscriptAsync(cli.ActiveSession!.Id);
        transcript.Should().HaveCountGreaterOrEqualTo(2);
        transcript.First().Role.Should().Be("user");
        transcript.First().Content.Should().Be("Can you explain how HUMofA works?");
        transcript.Last().Role.Should().Be("assistant");
        transcript.Last().Content.Should().NotBeEmpty();
    }

    [Fact]
    public void InteractiveCli_TokenStreamingTokenizer_RendersMarkdownCodeFencesAndHeaders()
    {
        var cli = CreateCli();
        string markdown = @"# Top Level Title
## Subheading
* Bullet item 1
- Bullet item 2
Here is some `inline code` to inspect.
```csharp
public class Worker
{
    public string Name { get; set; }
}
```";

        cli.RenderStreamingMarkdown(markdown);

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Top Level Title");
        output.Should().Contain("Subheading");
        output.Should().Contain("• Bullet item 1");
        output.Should().Contain("• Bullet item 2");
        output.Should().Contain("inline code");
        output.Should().Contain("csharp");
        output.Should().Contain("public class Worker");
        output.Should().Contain("1 │");
    }

    // ==========================================
    // 4. File Diff Preview & Confirmation Prompts
    // ==========================================

    [Fact]
    public async Task InteractiveCli_PromptFileModification_ApproveAppliesDiff()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string filePath = Path.Combine(_tempDir, "code.txt");
        await File.WriteAllTextAsync(filePath, "Original Line 1\nOriginal Line 2\n");

        _mockConsole.QueueChoice("y");

        bool applied = await cli.PromptFileModificationAsync(filePath, "Original Line 1\nModified Line 2\n");
        applied.Should().BeTrue();

        var updatedContent = await File.ReadAllTextAsync(filePath);
        updatedContent.Should().Contain("Modified Line 2");

        var snapshots = _diffService.GetSnapshots(filePath);
        snapshots.Should().NotBeEmpty();
    }

    [Fact]
    public async Task InteractiveCli_PromptFileModification_RejectLeavesFileUnmodified()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string filePath = Path.Combine(_tempDir, "reject_test.txt");
        await File.WriteAllTextAsync(filePath, "Untouched content\n");

        _mockConsole.QueueChoice("n");

        bool applied = await cli.PromptFileModificationAsync(filePath, "Changed content\n");
        applied.Should().BeFalse();

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Be("Untouched content\n");
    }

    [Fact]
    public async Task InteractiveCli_PromptFileModification_AlwaysAddsSessionGrant()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string filePath = Path.Combine(_tempDir, "auto_grant.txt");
        await File.WriteAllTextAsync(filePath, "Version 1\n");

        _mockConsole.QueueChoice("a");

        bool applied = await cli.PromptFileModificationAsync(filePath, "Version 2\n");
        applied.Should().BeTrue();

        bool hasGrant = await _sessionManager.CheckPermissionGrantAsync(cli.ActiveSession!.Id, "FileOps", filePath);
        hasGrant.Should().BeTrue();

        // Second modification should be pre-approved automatically without prompting
        bool secondApplied = await cli.PromptFileModificationAsync(filePath, "Version 3\n");
        secondApplied.Should().BeTrue();

        var finalContent = await File.ReadAllTextAsync(filePath);
        finalContent.Should().Contain("Version 3");
    }

    // ==========================================
    // 5. SmartApproval Interactive Prompts Tests
    // ==========================================

    [Fact]
    public async Task InteractiveCli_SmartApproval_GreenTierAutoApproves()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        bool approved = await cli.EvaluateSmartApprovalAsync("read_file", RiskTier.Green, "Read configuration file");
        approved.Should().BeTrue();

        _mockConsole.StrippedOutputText.Should().Contain("auto-approved (Green Tier)");
    }

    [Fact]
    public async Task InteractiveCli_SmartApproval_YellowTierInteractiveApproval()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        _mockConsole.QueueConfirmation(true);

        bool approved = await cli.EvaluateSmartApprovalAsync("write_config", RiskTier.Yellow, "Update application settings");
        approved.Should().BeTrue();

        _mockConsole.StrippedOutputText.Should().Contain("Yellow Tier");
    }

    [Fact]
    public async Task InteractiveCli_SmartApproval_RedTierPromptsAndHandlesYesNoAlways()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        // 1. Red tier Denied
        _mockConsole.QueueChoice("n");
        bool denied = await cli.EvaluateSmartApprovalAsync("rmdir_recursive", RiskTier.Red, "Delete directory");
        denied.Should().BeFalse();
        _mockConsole.StrippedOutputText.Should().Contain("Action denied by user.");

        // 2. Red tier Approved Once
        _mockConsole.QueueChoice("y");
        bool approvedOnce = await cli.EvaluateSmartApprovalAsync("delete_database", RiskTier.Red, "Wipe test db");
        approvedOnce.Should().BeTrue();

        // 3. Red tier Always Allow for session
        _mockConsole.QueueChoice("a");
        bool alwaysApproved = await cli.EvaluateSmartApprovalAsync("format_partition", RiskTier.Red, "Format volume");
        alwaysApproved.Should().BeTrue();

        // Check that grant was recorded in session
        bool hasGrant = await _sessionManager.CheckPermissionGrantAsync(cli.ActiveSession!.Id, "format_partition", "*");
        hasGrant.Should().BeTrue();

        // 4. Repeated action is now pre-approved
        bool preApproved = await cli.EvaluateSmartApprovalAsync("format_partition", RiskTier.Red, "Format volume");
        preApproved.Should().BeTrue();
        _mockConsole.StrippedOutputText.Should().Contain("pre-approved via session grant");
    }

    // ==========================================
    // 6. Session Resumption Tests
    // ==========================================

    [Fact]
    public async Task InteractiveCli_SessionResumption_LoadsPreviousTranscriptAndModel()
    {
        var session = await _sessionManager.CreateSessionAsync("Resumable Session", "claude-3-7-sonnet-latest", _tempDir);
        await _sessionManager.AppendMessageAsync(session.Id, "user", "Remember the secret key is 42");
        await _sessionManager.AppendMessageAsync(session.Id, "assistant", "I will remember the secret key is 42");

        var cli = CreateCli(initialSessionId: session.Id);
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        cli.ActiveSession.Should().NotBeNull();
        cli.ActiveSession!.Id.Should().Be(session.Id);
        cli.ActiveModel.Should().Be("claude-3-7-sonnet-latest");
    }

    // ==========================================
    // 7. Program.cs Arguments Parsing Smoke Tests
    // ==========================================

    [Fact]
    public async Task Program_Main_ListSessionsFlag_ReturnsZero()
    {
        string[] args = ["--list-sessions", "--workdir", _tempDir];
        int exitCode = await Program.Main(args);
        exitCode.Should().Be(0);
    }

    // ==========================================
    // 8. Milestone 5 Remediation Tests
    // ==========================================

    [Theory]
    [InlineData("no", "n")]
    [InlineData("reject", "n")]
    [InlineData("cancel", "n")]
    [InlineData("deny", "n")]
    [InlineData("n", "n")]
    [InlineData("yes", "y")]
    [InlineData("apply", "y")]
    [InlineData("approve", "y")]
    [InlineData("confirm", "y")]
    [InlineData("y", "y")]
    [InlineData("detail", "d")]
    [InlineData("details", "d")]
    [InlineData("diff", "d")]
    [InlineData("d", "d")]
    [InlineData("always", "a")]
    [InlineData("all", "a")]
    [InlineData("a", "a")]
    public void AnsiTerminal_ResolveChoice_SynonymsMapCorrectly(string input, string expected)
    {
        string[] options = ["y", "n", "d", "a"];
        string result = AnsiTerminal.ResolveChoice(input, options, defaultOption: "y");
        result.Should().Be(expected);
    }

    [Fact]
    public void AnsiTerminal_ResolveChoice_NoNeverFallsBackToYes()
    {
        string[] options = ["y", "n", "d", "a"];
        // When defaultOption is "y" and user types "no", it MUST resolve to "n", not "y"
        string result = AnsiTerminal.ResolveChoice("no", options, defaultOption: "y");
        result.Should().Be("n");
    }

    [Fact]
    public void AnsiTerminal_StripMarkup_OnlyStripsRecognizedTagsAndPreservesLiteralBrackets()
    {
        string markup = "[bold cyan]Title[/] with [Fact], [Theory], and item [1] or [d] button. [[escaped]] [bold green]OK[/]";
        string stripped = AnsiTerminal.StripMarkup(markup);

        // Literal brackets preserved
        stripped.Should().Contain("[Fact]");
        stripped.Should().Contain("[Theory]");
        stripped.Should().Contain("[1]");
        stripped.Should().Contain("[d]");
        stripped.Should().Contain("[escaped]");

        // Style tags stripped
        stripped.Should().NotContain("[bold cyan]");
        stripped.Should().NotContain("[/]");
        stripped.Should().NotContain("[bold green]");
    }

    [Fact]
    public async Task InteractiveCli_PromptFileModification_RejectWithFullWordNoLeavesFileUnmodified()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string filePath = Path.Combine(_tempDir, "reject_word_no.txt");
        await File.WriteAllTextAsync(filePath, "Original Content\n");

        _mockConsole.QueueChoice("no");

        bool applied = await cli.PromptFileModificationAsync(filePath, "Overwritten Content\n");
        applied.Should().BeFalse();

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Be("Original Content\n");
    }

    [Fact]
    public async Task InteractiveCli_PromptFileModification_RelativePath_AppliesToSessionWorkingDirectory()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        string relativeName = "relative_test_" + Guid.NewGuid().ToString("N") + ".txt";
        string targetFullPath = Path.Combine(cli.WorkingDirectory, relativeName);
        await File.WriteAllTextAsync(targetFullPath, "Initial version\n");

        _mockConsole.QueueChoice("yes");

        bool applied = await cli.PromptFileModificationAsync(relativeName, "Modified version\n");
        applied.Should().BeTrue();

        File.Exists(targetFullPath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(targetFullPath);
        content.Should().Be("Modified version\n");

        // Clean up
        if (File.Exists(targetFullPath))
        {
            File.Delete(targetFullPath);
        }
    }

    [Fact]
    public async Task InteractiveCli_InitializeSession_PrefixMatch_ResumesExistingSession()
    {
        var session = await _sessionManager.CreateSessionAsync("Prefix Resumable Session", "gpt-4o", _tempDir);
        string shortId = session.Id.Length > 8 ? session.Id[..8] : session.Id;

        var cli = CreateCli(initialSessionId: shortId);
        await cli.InitializeSessionAsync();

        cli.ActiveSession.Should().NotBeNull();
        cli.ActiveSession!.Id.Should().Be(session.Id);
        cli.ActiveModel.Should().Be("gpt-4o");
    }

    [Theory]
    [InlineData("git status", RiskTier.Green)]
    [InlineData("git status --short", RiskTier.Green)]
    [InlineData("git branch", RiskTier.Green)]
    [InlineData("git log -n 5", RiskTier.Green)]
    [InlineData("ls", RiskTier.Green)]
    [InlineData("ls -la", RiskTier.Green)]
    [InlineData("dir", RiskTier.Green)]
    [InlineData("dir /w", RiskTier.Green)]
    [InlineData("echo hello world", RiskTier.Green)]
    [InlineData("pwd", RiskTier.Green)]
    [InlineData("dotnet build", RiskTier.Yellow)]
    [InlineData("npm test", RiskTier.Yellow)]
    [InlineData("rmdir /s /q temp", RiskTier.Yellow)]
    [InlineData("git push origin main", RiskTier.Yellow)]
    [InlineData("rm -rf /", RiskTier.Red)]
    [InlineData("drop database prod", RiskTier.Red)]
    public void SmartApproval_ClassifyAction_ShellExecution_ProperlyTiered(string command, RiskTier expectedTier)
    {
        var approval = new SmartApproval();
        var parameters = new Dictionary<string, string> { ["cmd"] = command };

        var tierExec = approval.ClassifyAction("/exec", parameters);
        tierExec.Should().Be(expectedTier);

        var tierTerminal = approval.ClassifyAction("terminal", parameters);
        tierTerminal.Should().Be(expectedTier);
    }

    [Fact]
    public void InteractiveCli_Autocomplete_ProvidesParameterHintsForTopLevelCommands()
    {
        var hints = InteractiveCli.CommandParameterHints;
        hints.Should().Contain(h => h.StartsWith("/model "));
        hints.Should().Contain(h => h.StartsWith("/skill "));
        hints.Should().Contain(h => h.StartsWith("/sessions "));

        var modelSuggestions = InteractiveCli.GetAutocompleteSuggestions("/model ");
        modelSuggestions.Should().NotBeEmpty();
        modelSuggestions.Should().AllSatisfy(s => s.StartsWith("/model "));
    }

    [Fact]
    public async Task InteractiveCli_SingleSlash_RendersCommandReference()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Komut ve Araç Referansı");
        output.Should().Contain("/subagent");
        output.Should().Contain("/model");
    }

    [Fact]
    public async Task InteractiveCli_SubAgentCommand_ListsAndCreatesSubAgent()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/subagent list");
        _mockConsole.QueueInput("/subagent create tester \"Test Runner\"");
        _mockConsole.QueueInput("/subagent list");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Autonomous Sub-Agents & Personas");
        output.Should().Contain("Successfully created sub-agent");
        output.Should().Contain("Test Runner");
    }

    [Fact]
    public async Task InteractiveCli_SubAgentCommand_DeletesSubAgentSuccessfully()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/subagent create testbot \"Automation Bot\"");
        _mockConsole.QueueInput("/subagent delete testbot");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Successfully created sub-agent");
        output.Should().Contain("Successfully deleted sub-agent: testbot");
        _agentManager.GetAgent("testbot").Should().BeNull();
    }

    [Fact]
    public async Task InteractiveCli_AgentCommand_DeletesAgentSuccessfully()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/agent create bot1 \"Assistant One\"");
        _mockConsole.QueueInput("/agent delete bot1");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("Successfully registered agent");
        output.Should().Contain("Successfully deleted agent: bot1");
        _agentManager.GetAgent("bot1").Should().BeNull();
    }

    [Fact]
    public void InteractiveCli_ParseToolInvocations_ParsesAllToolTagsCorrectly()
    {
        string sampleModelResponse = """
I will check the environment and write the file.

<bash>
ffmpeg -version
</bash>

<write_file path="test_output.txt">
Hello from HUMofA autonomous tool loop!
</write_file>

<read_file path="test_output.txt" />

<subagent role="UnitTester" name="SubAgent1">
Run all automated test suites and report findings.
</subagent>
""";

        var tools = InteractiveCli.ParseToolInvocations(sampleModelResponse);
        tools.Should().HaveCount(4);

        tools[0].ToolType.Should().Be("bash");
        tools[0].Content.Should().Be("ffmpeg -version");

        tools[1].ToolType.Should().Be("write_file");
        tools[1].Parameter.Should().Be("test_output.txt");
        tools[1].Content.Should().Contain("Hello from HUMofA autonomous tool loop!");

        tools[2].ToolType.Should().Be("read_file");
        tools[2].Parameter.Should().Be("test_output.txt");

        tools[3].ToolType.Should().Be("subagent");
        tools[3].Parameter.Should().Be("UnitTester");
        tools[3].Content.Should().Contain("Run all automated test suites");
    }

    [Fact]
    public void InteractiveCli_ParseToolInvocations_ParsesAskUserTagWithReasonAndOptions()
    {
        string responseWithAskUser = """
I encountered an authentication screen and need your help.

<ask_user reason="Instagram giriş doğrulaması gerekiyor" options="Giriş yaptım devam et|İşlemi iptal et">
Lütfen açılan pencerede 2FA kodunu girin ve tamamlandığında bildirin.
</ask_user>
""";

        var tools = InteractiveCli.ParseToolInvocations(responseWithAskUser);
        tools.Should().HaveCount(1);
        tools[0].ToolType.Should().Be("ask_user");
        tools[0].Parameter.Should().Be("Instagram giriş doğrulaması gerekiyor");
        tools[0].ExtraOptions.Should().Be("Giriş yaptım devam et|İşlemi iptal et");
        tools[0].Content.Should().Contain("Lütfen açılan pencerede 2FA kodunu girin");
    }

    [Fact]
    public async Task InteractiveCli_BtwAndDerivativeCommands_ExecuteCleanlyWithoutPollutingMainTranscript()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/btw C# 13 yenilikleri nelerdir?");
        _mockConsole.QueueInput("/note Proje .NET 10 kullanıyor");
        _mockConsole.QueueInput("/status");
        _mockConsole.QueueInput("/think off");
        _mockConsole.QueueInput("/think on");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var output = _mockConsole.StrippedOutputText;
        output.Should().Contain("[BTW · Yan Sorgu]");
        output.Should().Contain("Not aktif oturum bağlamına ve hafızaya eklendi");
        output.Should().Contain("HUMofA Oturum, Model ve Sistem Durumu");
        output.Should().Contain("AÇILDI");

        // Verify /btw did NOT pollute the main session transcript, while /note did record the note
        var transcript = await _sessionManager.GetTranscriptAsync(cli.ActiveSession!.Id);
        transcript.Should().ContainSingle(m => m.Content.Contains("Proje .NET 10 kullanıyor"));
        transcript.Should().NotContain(m => m.Content.Contains("C# 13 yenilikleri nelerdir?"));
    }
}
