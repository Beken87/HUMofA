using System.Reflection;
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
/// Empirical challenge test suite for Milestone 5: Rich Interactive CLI & Terminal Experience.
/// Authored by challenger_m5_1.
/// Adversarially tests all 14 slash commands, token streaming markdown tokenizer,
/// braille spinner animation steps, tool execution cards, and edge-case handling.
/// </summary>
public class Milestone5Challenger1Tests : IDisposable
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

    public Milestone5Challenger1Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "humofa-challenger1-m5-" + Guid.NewGuid().ToString("N"));
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
            // Ignore directory lock during test cleanup
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

    // =========================================================================
    // PART 1: ALL 14 SLASH COMMANDS EMPIRICAL TESTS
    // (/model, /agent, /skill, /sessions, /diff, /clear, /compact, /memory,
    //  /kanban, /exec, /permissions, /help, /exit, /quit + Unknown command)
    // =========================================================================

    [Fact]
    public async Task SlashCommand_Model_ListsProviders_AndSwitchesModelWithSessionUpdate()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        // 1. Without args: lists built-in models
        _mockConsole.QueueInput("/model");
        // 2. With args: switches to claude-3-7-sonnet-latest
        _mockConsole.QueueInput("/model claude-3-7-sonnet-latest");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        cli.ActiveModel.Should().Be("claude-3-7-sonnet-latest");

        // Verify updated in SQLite session store
        var updatedSession = await _sessionManager.GetSessionAsync(cli.ActiveSession!.Id);
        updatedSession.Should().NotBeNull();
        updatedSession!.Model.Should().Be("claude-3-7-sonnet-latest");

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Available Built-in Models");
        text.Should().Contain("Switched active model to: claude-3-7-sonnet-latest");
    }

    [Fact]
    public async Task SlashCommand_Agent_HandlesListCreateSwitchAndMissingArgs()
    {
        var cli = CreateCli();
        // 1. List registered agents
        _mockConsole.QueueInput("/agent list");
        // 2. Create missing args
        _mockConsole.QueueInput("/agent create");
        // 3. Create agent with quotes
        _mockConsole.QueueInput("/agent create tester \"QA Challenger Agent\"");
        // 4. Switch missing args
        _mockConsole.QueueInput("/agent switch");
        // 5. Switch to nonexistent agent
        _mockConsole.QueueInput("/agent switch nonexistent_agent");
        // 6. Switch to newly created agent
        _mockConsole.QueueInput("/agent switch tester");
        // 7. Unknown subaction
        _mockConsole.QueueInput("/agent destroy tester");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Registered Agents");
        text.Should().Contain("default-agent");
        text.Should().Contain("Usage: /agent create <id> <name>");
        text.Should().Contain("Successfully registered agent: QA Challenger Agent");
        text.Should().Contain("Usage: /agent switch <id>");
        text.Should().Contain("Agent 'nonexistent_agent' not found.");
        text.Should().Contain("Switched to agent: QA Challenger Agent");
        text.Should().Contain("Unknown agent action 'destroy'");

        var registered = _agentManager.GetAgent("tester");
        registered.Should().NotBeNull();
        registered!.Name.Should().Be("QA Challenger Agent");
    }

    [Fact]
    public async Task SlashCommand_Skill_HandlesListRunAndInstall()
    {
        string skillDoc = @"---
name: sec-audit
description: Security auditing and vulnerability scanner
provider: enterprise
---
# Security Audit Instructions
Scan dependencies for CVEs.";
        await _skillManager.InstallSkillFromMarkdownAsync(skillDoc);

        var cli = CreateCli();
        // 1. List
        _mockConsole.QueueInput("/skill list");
        // 2. Run existing
        _mockConsole.QueueInput("/skill run sec-audit");
        // 3. Run nonexistent
        _mockConsole.QueueInput("/skill run ghost-skill");
        // 4. Run missing args
        _mockConsole.QueueInput("/skill run");
        // 5. Install missing args
        _mockConsole.QueueInput("/skill install");
        // 6. Unknown action
        _mockConsole.QueueInput("/skill deprecate sec-audit");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Installed Skills");
        text.Should().Contain("sec-audit");
        text.Should().Contain("Security auditing and vulnerability scanner");
        text.Should().Contain("Skill: sec-audit");
        text.Should().Contain("Scan dependencies for CVEs");
        text.Should().Contain("Skill 'ghost-skill' not found.");
        text.Should().Contain("Usage: /skill run <name>");
        text.Should().Contain("Usage: /skill install <repo-or-url>");
        text.Should().Contain("Unknown skill action 'deprecate'");
    }

    [Fact]
    public async Task SlashCommand_Sessions_HandlesListNewResumePrefixAndNotFound()
    {
        var targetSession = await _sessionManager.CreateSessionAsync("Target Milestone 5", "gemini-2.5-flash", _tempDir);
        await _sessionManager.AppendMessageAsync(targetSession.Id, "user", "Hello milestone 5 verification");

        var cli = CreateCli();
        // 1. List
        _mockConsole.QueueInput("/sessions list");
        // 2. Resume missing args
        _mockConsole.QueueInput("/sessions resume");
        // 3. Resume nonexistent ID
        _mockConsole.QueueInput("/sessions resume invalid_id_99999");
        // 4. Resume by prefix
        string prefix = targetSession.Id.Substring(0, 8);
        _mockConsole.QueueInput($"/sessions resume {prefix}");
        // 5. Create new session with quotes
        _mockConsole.QueueInput("/sessions new \"Challenge Verification Session\"");
        // 6. Unknown action
        _mockConsole.QueueInput("/sessions archive 123");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Persistent Sessions");
        text.Should().Contain("Target Milestone 5");
        text.Should().Contain("Usage: /sessions resume <id>");
        text.Should().Contain("Session 'invalid_id_99999' not found.");
        text.Should().Contain("Resumed session: Target Milestone 5");
        text.Should().Contain("Hello milestone 5 verification");
        text.Should().Contain("Created and switched to new session: Challenge Verification Session");
        text.Should().Contain("Unknown session action 'archive'");
    }

    [Fact]
    public async Task SlashCommand_Diff_HandlesEmptySnapshots_NonexistentFile_AndDiffRendering()
    {
        string untouchedFile = Path.Combine(_tempDir, "untouched_file.cs");
        await File.WriteAllTextAsync(untouchedFile, "public class Untouched { }\n");

        string modifiedFile = Path.Combine(_tempDir, "patch_test.cs");
        await File.WriteAllTextAsync(modifiedFile, "public class Sample { int x = 1; }\n");

        // Create snapshot for modifiedFile
        await _diffService.CreateSnapshotAsync(modifiedFile);
        await File.WriteAllTextAsync(modifiedFile, "public class Sample { int x = 2; int y = 3; }\n");

        var cli = CreateCli();
        // 1. List recent snapshots
        _mockConsole.QueueInput("/diff");
        // 2. Diff nonexistent file
        _mockConsole.QueueInput("/diff does_not_exist.txt");
        // 3. Diff existing file with NO snapshots
        _mockConsole.QueueInput($"/diff {Path.GetFileName(untouchedFile)}");
        // 4. Diff existing file with snapshot
        _mockConsole.QueueInput($"/diff {Path.GetFileName(modifiedFile)}");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Recent File Rollback Snapshots");
        text.Should().Contain("File 'does_not_exist.txt' does not exist.");
        text.Should().Contain("No snapshots found for 'untouched_file.cs'");
        _mockConsole.RenderedDiffs.Should().ContainSingle();
        _mockConsole.RenderedDiffs[0].AdditionsCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SlashCommand_Clear_InvokesClearScreenAndRedrawsHeader()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/clear");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        // Raw output must contain the ANSI clear sequence \x1b[2J\x1b[H
        _mockConsole.RawOutputText.Should().Contain("\x1b[2J\x1b[H");
        _mockConsole.StrippedOutputText.Should().Contain("HUMofA Interactive Terminal");
    }

    [Fact]
    public async Task SlashCommand_Compact_ReducesTokenFootprintAndAppendsToMemory()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        // Seed messages to give it tokens
        await _sessionManager.AppendMessageAsync(cli.ActiveSession!.Id, "user", new string('X', 400), tokenCount: 100);
        await _sessionManager.AppendMessageAsync(cli.ActiveSession!.Id, "assistant", new string('Y', 800), tokenCount: 200);

        _mockConsole.QueueInput("/compact");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Session compacted successfully");

        // Verify MEMORY.md was updated
        var memoryHits = await _memoryStore.SearchMemoriesAsync("compacted", limit: 5);
        memoryHits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SlashCommand_Memory_HandlesStatusAddAndQuery()
    {
        await _memoryStore.InitializeAsync();

        var cli = CreateCli();
        // 1. Status
        _mockConsole.QueueInput("/memory");
        // 2. Add missing args
        _mockConsole.QueueInput("/memory add");
        // 3. Add valid entry
        _mockConsole.QueueInput("/memory add DeploymentRule \"Always use Blue-Green deployments in production.\"");
        // 4. Query missing args
        _mockConsole.QueueInput("/memory query");
        // 5. Query valid terms
        _mockConsole.QueueInput("/memory query Blue-Green");
        // 6. Query with no results
        _mockConsole.QueueInput("/memory query NonExistentKeywordXYZ");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Unified Memory Store Status");
        text.Should().Contain("SOUL.md");
        text.Should().Contain("MEMORY.md");
        text.Should().Contain("Usage: /memory add <SectionName> <Text>");
        text.Should().Contain("Appended entry under ## DeploymentRule to MEMORY.md");
        text.Should().Contain("Usage: /memory query <search terms>");
        text.Should().Contain("Memory Search: 'Blue-Green'");
        text.Should().Contain("Blue-Green deployments");
        text.Should().Contain("No matching memory records found.");
    }

    [Fact]
    public async Task SlashCommand_Kanban_RendersAllThreeColumnsWithPopulatedAgents()
    {
        // Register agents in different statuses
        _agentManager.RegisterAgent(new AgentConfig("todo-agent", "Worker One", "You are worker one."));
        _agentManager.RegisterAgent(new AgentConfig("running-agent", "Worker Two", "You are worker two."));
        _agentManager.RegisterAgent(new AgentConfig("done-agent", "Worker Three", "You are worker three."));

        _agentManager.UpdateAgentStatus("running-agent", AgentStatus.Running);
        _agentManager.UpdateAgentStatus("done-agent", AgentStatus.Completed);

        var cli = CreateCli();
        _mockConsole.QueueInput("/kanban");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Sub-Agent Kanban Board");
        text.Should().Contain("TODO");
        text.Should().Contain("IN PROGRESS");
        text.Should().Contain("DONE");
        text.Should().Contain("Worker One");
        text.Should().Contain("Worker Two");
        text.Should().Contain("Worker Three");
    }

    [Fact]
    public async Task SlashCommand_Exec_HandlesMissingArgs_InvalidBackend_DeniedApproval_AndSuccessfulExecution()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        // 1. Missing args
        _mockConsole.QueueInput("/exec");
        // 2. Unknown backend
        _mockConsole.QueueInput("/exec Kubernetes kubectl get pods");
        // 3. Red-tier command rejected by user (contains "rm -rf" which is classified as Red)
        _mockConsole.QueueInput("/exec Local rm -rf /critical/path");
        _mockConsole.QueueChoice("n"); // Deny
        // 4. Green-tier command allowed
        _mockConsole.QueueInput("/exec Local echo HelloTerminalExecution");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Usage: /exec <backend> <command>");
        text.Should().Contain("Unknown backend 'Kubernetes'");
        text.Should().Contain("Action denied by user.");
        text.Should().Contain("Command execution denied.");
        text.Should().Contain("Exec (Local)");
        _mockConsole.RenderedCards.Should().Contain(c => c.ToolName.Contains("Exec"));
    }

    [Fact]
    public async Task SlashCommand_Permissions_ListsAndClearsGrantsCorrectly()
    {
        var cli = CreateCli();
        await cli.InitializeSessionAsync();

        // Add 2 grants
        await _sessionManager.AddPermissionGrantAsync(cli.ActiveSession!.Id, "Terminal", "git *", "Yellow");
        await _sessionManager.AddPermissionGrantAsync(cli.ActiveSession!.Id, "FileOps", "src/**/*.cs", "Green");

        // 1. List grants
        _mockConsole.QueueInput("/permissions");
        // 2. Clear grants
        _mockConsole.QueueInput("/permissions clear");
        // 3. List again (empty)
        _mockConsole.QueueInput("/permissions");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Active Session Permission Grants");
        text.Should().Contain("Tool: Terminal - Pattern: git *");
        text.Should().Contain("Tool: FileOps - Pattern: src/**/*.cs");
        text.Should().Contain("Cleared 2 session-scoped permission grants");
        text.Should().Contain("No persistent session-scoped permission grants active.");
    }

    [Fact]
    public async Task SlashCommand_Help_ListsAllFourteenCommands()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/help");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Command Reference");
        text.Should().Contain("/model");
        text.Should().Contain("/agent");
        text.Should().Contain("/skill");
        text.Should().Contain("/sessions");
        text.Should().Contain("/diff");
        text.Should().Contain("/memory");
        text.Should().Contain("/kanban");
        text.Should().Contain("/exec");
        text.Should().Contain("/permissions");
        text.Should().Contain("/compact");
        text.Should().Contain("/clear");
        text.Should().Contain("/exit");
    }

    [Fact]
    public async Task SlashCommand_Quit_TerminatesSessionCleanlyLikeExit()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/quit");

        await cli.RunAsync();

        cli.IsRunning.Should().BeFalse();
        _mockConsole.StrippedOutputText.Should().Contain("Session terminated cleanly. Goodbye!");
    }

    [Fact]
    public async Task SlashCommand_UnknownCommand_RendersHelpfulErrorMessage()
    {
        var cli = CreateCli();
        _mockConsole.QueueInput("/foo_bar_command");
        _mockConsole.QueueInput("/exit");

        await cli.RunAsync();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Unknown command '/foo_bar_command'. Type /help to view available commands.");
    }

    // =========================================================================
    // PART 2: TOKEN STREAMING MARKDOWN TOKENIZER TESTS
    // (Multi-line code fences with language tags, line numbers, inline code,
    //  bold text, bullet lists, headers, mixed complex markdown)
    // =========================================================================

    [Fact]
    public void MarkdownTokenizer_MultiLineCodeFence_RendersHeadersLineNumbersAndBottomBorder()
    {
        var cli = CreateCli();
        string csharpCode = @"```csharp
public class OrderProcessor
{
    private readonly ILogger _logger;

    public async Task<bool> ProcessOrderAsync(int orderId)
    {
        var valid = orderId > 0;
        if (valid)
        {
            return true;
        }
        return false;
    }
}
```";
        cli.RenderStreamingMarkdown(csharpCode);

        var text = _mockConsole.StrippedOutputText;
        // Language tag
        text.Should().Contain("csharp");
        // Top and bottom borders
        text.Should().Contain("┌──");
        text.Should().Contain("└───");
        // Line number gutters
        text.Should().Contain("   1 │");
        text.Should().Contain("   2 │");
        text.Should().Contain("   6 │");
        text.Should().Contain("  14 │");

        // Keyword highlighting present in raw ansi/markup
        var raw = _mockConsole.RawOutputText;
        raw.Should().Contain("public");
        raw.Should().Contain("class");
        raw.Should().Contain("async");
        raw.Should().Contain("return");
    }

    [Fact]
    public void MarkdownTokenizer_CodeFenceWithoutLanguageTag_DefaultsToCode()
    {
        var cli = CreateCli();
        string rawCode = @"```
line without language
another line
```";
        cli.RenderStreamingMarkdown(rawCode);

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("code");
        text.Should().Contain("1 │");
        text.Should().Contain("line without language");
        text.Should().Contain("2 │");
        text.Should().Contain("another line");
    }

    [Fact]
    public void MarkdownTokenizer_InlineBackticksAndBoldText_FormatsProperly()
    {
        var cli = CreateCli();
        string mixedLine = "Verify the `OrderProcessor.cs` class using **strict assertion** rules.";
        cli.RenderStreamingMarkdown(mixedLine);

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Verify the OrderProcessor.cs class using strict assertion rules.");

        var raw = _mockConsole.RawOutputText;
        // Inline code -> [yellow] tag -> \x1b[33m
        raw.Should().Contain("\x1b[33mOrderProcessor.cs\x1b[0m");
        // Bold -> [bold] tag -> \x1b[1m
        raw.Should().Contain("\x1b[1mstrict assertion\x1b[0m");
    }

    [Fact]
    public void MarkdownTokenizer_BulletLists_FormatsBothAsteriskAndDashStyles()
    {
        var cli = CreateCli();
        string bullets = @"* First asterisk item
- Second dash item
* Third item with `inline_code` and **bold text**";

        cli.RenderStreamingMarkdown(bullets);

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("• First asterisk item");
        text.Should().Contain("• Second dash item");
        text.Should().Contain("• Third item with inline_code and bold text");
    }

    [Fact]
    public void MarkdownTokenizer_HeaderLevels_FormatsH1H2AndH3Differently()
    {
        var cli = CreateCli();
        string headers = @"# Primary Architecture Level 1
## Secondary Subsystem Level 2
### Detailed Component Level 3";

        cli.RenderStreamingMarkdown(headers);

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("Primary Architecture Level 1");
        text.Should().Contain("Secondary Subsystem Level 2");
        text.Should().Contain("Detailed Component Level 3");

        var raw = _mockConsole.RawOutputText;
        // H1 -> bright-cyan \x1b[96m
        raw.Should().Contain("\x1b[96mPrimary Architecture Level 1");
        // H2 -> cyan \x1b[36m
        raw.Should().Contain("\x1b[36mSecondary Subsystem Level 2");
        // H3 -> yellow \x1b[33m
        raw.Should().Contain("\x1b[33mDetailed Component Level 3");
    }

    [Fact]
    public void MarkdownTokenizer_ComplexMixedDocument_RendersAllComponentsWithoutCrashing()
    {
        var cli = CreateCli();
        string fullDoc = @"# System Architecture Verification
The **HUMofA Platform** integrates `Hermes` and `Claude Code` principles.

## Core Services
* `SessionManager`: SQLite WAL persistent session database
- `AgentManager`: Subagent orchestration
* `FileDiffService`: **Myers diff** algorithm with rollback snapshots

### Implementation Sample
```typescript
interface AgentMessage {
    id: string;
    content: string;
    timestamp: Date;
}
```

Final verification: all systems **OPERATIONAL**.";

        cli.RenderStreamingMarkdown(fullDoc);

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("System Architecture Verification");
        text.Should().Contain("HUMofA Platform");
        text.Should().Contain("Core Services");
        text.Should().Contain("• SessionManager");
        text.Should().Contain("• AgentManager");
        text.Should().Contain("• FileDiffService");
        text.Should().Contain("typescript");
        text.Should().Contain("interface AgentMessage");
        text.Should().Contain("Final verification: all systems OPERATIONAL.");
    }

    // =========================================================================
    // PART 3: BRAILLE SPINNER ANIMATION & TOOL EXECUTION CARD TESTS
    // =========================================================================

    [Fact]
    public async Task BrailleSpinner_AnimationSteps_CyclesFramesAndClearsLine()
    {
        // Access private BrailleSpinner via reflection to test animation steps empirically
        var spinnerType = typeof(InteractiveCli).GetNestedType("BrailleSpinner", BindingFlags.NonPublic);
        spinnerType.Should().NotBeNull();

        var spinnerInstance = Activator.CreateInstance(spinnerType!, _mockConsole, "Reasoning step...");
        spinnerInstance.Should().NotBeNull();

        var startMethod = spinnerType!.GetMethod("Start");
        var stopMethod = spinnerType.GetMethod("Stop");

        startMethod.Should().NotBeNull();
        stopMethod.Should().NotBeNull();

        // Start spinner and let it cycle for ~260ms (approx 3 frames at 80ms interval)
        startMethod!.Invoke(spinnerInstance, null);
        await Task.Delay(260);
        stopMethod!.Invoke(spinnerInstance, null);

        // Verify that raw output received spinner frame characters
        var raw = _mockConsole.RawOutputText;
        // Braille spinner frames: "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"
        bool containsBraille = raw.Contains("⠋") || raw.Contains("⠙") || raw.Contains("⠹") || raw.Contains("⠸");
        containsBraille.Should().BeTrue("Spinner must output braille animation frames");

        // Verify ClearLine was called (emits \r\x1b[2K)
        raw.Should().Contain("\r\x1b[2K");
        raw.Should().Contain("Reasoning step...");

        // Ensure safe disposal
        if (spinnerInstance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public void ToolExecutionCard_SuccessCard_RendersGreenCheckAndTiming()
    {
        var card = new ToolExecutionCard(
            ToolName: "FileRead",
            Parameters: "path='src/HUMofA/Program.cs'",
            Output: "namespace HUMofA;\nclass Program { }",
            ExecutionTimeMs: 18,
            IsSuccess: true,
            IsFolded: false);

        _mockConsole.RenderCard(card);

        _mockConsole.RenderedCards.Should().ContainSingle();
        var recorded = _mockConsole.RenderedCards[0];
        recorded.ToolName.Should().Be("FileRead");
        recorded.IsSuccess.Should().BeTrue();

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("┌─ ⚡ FileRead");
        text.Should().Contain("Parameters: path='src/HUMofA/Program.cs'");
        text.Should().Contain("Completed in 18ms");
        text.Should().Contain("Output (2 lines):");
        text.Should().Contain("class Program { }");
        text.Should().Contain("└");
    }

    [Fact]
    public void ToolExecutionCard_FailureCard_RendersRedCrossAndFailureStatus()
    {
        var card = new ToolExecutionCard(
            ToolName: "BashExecution",
            Parameters: "cmd='exit 1'",
            Output: "Command failed with code 1: permission denied",
            ExecutionTimeMs: 145,
            IsSuccess: false,
            IsFolded: false);

        _mockConsole.RenderCard(card);

        var text = _mockConsole.StrippedOutputText;
        text.Should().Contain("┌─ ⚡ BashExecution");
        text.Should().Contain("Status: ✖ Failed (145ms)");
        text.Should().Contain("permission denied");
    }

    [Fact]
    public void ToolExecutionCard_FoldedOutput_HidesMiddleLinesWhenExceedingLimit()
    {
        var manyLines = Enumerable.Range(1, 30).Select(i => $"Execution log line {i}").ToList();
        var card = new ToolExecutionCard(
            ToolName: "LongRunningBuild",
            Parameters: "target=all",
            Output: string.Join("\n", manyLines),
            ExecutionTimeMs: 820,
            IsSuccess: true,
            IsFolded: true);

        var folded = card.GetFoldedLines();
        folded.Should().HaveCount(8); // 4 top + 1 hidden summary + 3 bottom = 8 lines

        folded[0].Should().Be("Execution log line 1");
        folded[3].Should().Be("Execution log line 4");
        folded[4].Should().Contain("23 lines hidden");
        folded[5].Should().Be("Execution log line 28");
        folded[7].Should().Be("Execution log line 30");

        _mockConsole.RenderCard(card);

        // In raw output, the full folded message with escape markup is rendered
        _mockConsole.RawOutputText.Should().Contain("23 lines hidden");
        _mockConsole.RawOutputText.Should().Contain("Execution log line 1");
        _mockConsole.RawOutputText.Should().Contain("Execution log line 30");
        _mockConsole.RawOutputText.Should().NotContain("Execution log line 15");
    }

    [Fact]
    public void ToolExecutionCard_UnfoldedCard_ShowsAllLinesRegardlessOfCount()
    {
        var manyLines = Enumerable.Range(1, 20).Select(i => $"Line {i}").ToList();
        var card = new ToolExecutionCard(
            ToolName: "VerboseDump",
            Parameters: "none",
            Output: string.Join("\n", manyLines),
            ExecutionTimeMs: 50,
            IsSuccess: true,
            IsFolded: false);

        var lines = card.GetFoldedLines();
        lines.Should().HaveCount(20);

        _mockConsole.RenderCard(card);
        var text = _mockConsole.StrippedOutputText;
        text.Should().NotContain("hidden");
        text.Should().Contain("Line 1");
        text.Should().Contain("Line 10");
        text.Should().Contain("Line 20");
    }

    [Fact]
    public void ToolExecutionCard_EscapesMarkupTagsInParametersAndOutput()
    {
        var card = new ToolExecutionCard(
            ToolName: "SecurityTool",
            Parameters: "tags=[bold] [red] [cyan] [unknown]",
            Output: "Detected payload: [script]alert('xss')[/script]",
            ExecutionTimeMs: 12,
            IsSuccess: true,
            IsFolded: false);

        // Rendering must not throw even with bracket markup characters
        var action = () => _mockConsole.RenderCard(card);
        action.Should().NotThrow();

        // Raw output preserves the escaped tags
        _mockConsole.RawOutputText.Should().Contain("SecurityTool");
        _mockConsole.RawOutputText.Should().Contain("tags=");
        _mockConsole.RawOutputText.Should().Contain("Detected payload:");
    }

    // =========================================================================
    // PART 4: PROMPT BADGE CONTEXT & STATUS FORMATTING EDGE CASES
    // =========================================================================

    [Theory]
    [InlineData(0, "0 tokens")]
    [InlineData(500, "500 tokens")]
    [InlineData(1000, "1.0k tokens")]
    [InlineData(45200, "45.2k tokens")]
    [InlineData(1000000, "1.0M tokens")]
    [InlineData(2750000, "2.8M tokens")]
    public void PromptBadgeContext_FormatTokens_FormatsAccurately(int count, string expected)
    {
        var badge = new PromptBadgeContext(
            WorkingDirectory: "C:\\repo",
            GitBranch: "main",
            ActiveModel: "gpt-4o",
            SessionId: "s1",
            TokenCount: count);

        badge.FormatTokens().Should().Be(expected);
    }

    [Theory]
    [InlineData("", "none")]
    [InlineData("s123", "s123")]
    [InlineData("sess_1234567890abcdef", "123456")]
    [InlineData("custom_session_long_id", "custom_s")]
    public void PromptBadgeContext_FormatShortSessionId_FormatsAccurately(string sessionId, string expected)
    {
        var badge = new PromptBadgeContext(
            WorkingDirectory: "C:\\repo",
            GitBranch: "main",
            ActiveModel: "gpt-4o",
            SessionId: sessionId,
            TokenCount: 100);

        badge.FormatShortSessionId().Should().Be(expected);
    }
}
