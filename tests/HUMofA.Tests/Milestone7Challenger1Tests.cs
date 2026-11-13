using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HUMofA.Desktop;
using Xunit;

namespace HUMofA.Tests;

/// <summary>
/// Empirical challenge test suite for Milestone 7: Production Desktop Workspace Application UI.
/// Authored by challenger_m7_1.
/// Strictly verifies:
/// 1. Static asset delivery over dynamic Kestrel host: GET /, GET /app.js, GET /style.css (headers, content types, non-empty payloads).
/// 2. Structural presence of all 11 sidebar views and their interactive control IDs in index.html.
/// 3. Wires real fetch calls in app.js to backend endpoints (/api/tasks, /api/sessions, /api/fs/diff/approve, /api/fs/diff/reject, /api/terminal/exec, /api/settings, etc.).
/// 4. End-to-end task dispatching and file modification approval flow simulating UI interaction.
/// </summary>
public class Milestone7Challenger1Tests
{
    private static string ResolveUiDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "HUMofA", "Desktop", "UI");
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(current.FullName, "Desktop", "UI");
            if (Directory.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "Desktop", "UI");
    }

    private static async Task<(DesktopHost Host, HttpClient Client, string WorkDir, string UiDir)> CreateTestServerAsync()
    {
        var tempWorkDir = Path.Combine(Path.GetTempPath(), "humofa-m7-challenge1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkDir);
        var uiDir = ResolveUiDirectory();
        var host = new DesktopHost(workspaceDir: tempWorkDir, uiRootDir: uiDir);
        var baseUrl = await host.StartAsync(port: 0);
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (host, client, tempWorkDir, uiDir);
    }

    // =========================================================================
    // SECTION 1: STATIC ASSET DELIVERY OVER KESTREL HOST
    // =========================================================================

    [Fact]
    public async Task StaticAssetDelivery_RootPath_ServesProductionHtmlWithProperHeadersAndSize()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var res = await client.GetAsync("/");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
            res.Content.Headers.ContentType?.CharSet.Should().Be("utf-8");

            var html = await res.Content.ReadAsStringAsync();
            html.Should().NotBeNullOrWhiteSpace();
            html.Length.Should().BeGreaterThan(30000, "Production index.html should be a rich, comprehensive document (>30KB)");
            html.Should().StartWith("<!DOCTYPE html>");
            html.Should().Contain("<html lang=\"en\" data-theme=\"dark\">");
            html.Should().Contain("<link rel=\"stylesheet\" href=\"/style.css\" />");
            html.Should().Contain("<script src=\"/app.js\"></script>");
        }
    }

    [Fact]
    public async Task StaticAssetDelivery_AppJs_ServesProductionJavaScriptWithProperHeadersAndSize()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var res = await client.GetAsync("/app.js");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Content.Headers.ContentType?.MediaType.Should().Be("application/javascript");
            res.Content.Headers.ContentType?.CharSet.Should().Be("utf-8");

            var js = await res.Content.ReadAsStringAsync();
            js.Should().NotBeNullOrWhiteSpace();
            js.Length.Should().BeGreaterThan(40000, "Production app.js should be an exhaustive client implementation (>40KB)");
            js.Should().Contain("const state =");
            js.Should().Contain("const api =");
            js.Should().Contain("function initWebSocket()");
            js.Should().Contain("function handleWsFrame(");
            js.Should().Contain("function escapeHtml(");
        }
    }

    [Fact]
    public async Task StaticAssetDelivery_StyleCss_ServesProductionCssWithProperHeadersAndSize()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var res = await client.GetAsync("/style.css");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Content.Headers.ContentType?.MediaType.Should().Be("text/css");
            res.Content.Headers.ContentType?.CharSet.Should().Be("utf-8");

            var css = await res.Content.ReadAsStringAsync();
            css.Should().NotBeNullOrWhiteSpace();
            css.Length.Should().BeGreaterThan(15000, "Production style.css should contain full design tokens and component styling (>15KB)");
            css.Should().Contain(":root");
            css.Should().Contain("[data-theme=\"light\"]");
            css.Should().Contain("--bg-primary");
            css.Should().Contain("--accent-primary");
            css.Should().Contain(".diff-line.add");
            css.Should().Contain(".diff-line.del");
            css.Should().Contain(".diff-line.hunk");
            css.Should().Contain(".resize-handle");
            css.Should().Contain(".command-palette-card");
        }
    }

    [Fact]
    public async Task StaticAssetDelivery_MissingAsset_Returns404NotFound()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var res = await client.GetAsync("/nonexistent-bundle.js");
            res.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var res2 = await client.GetAsync("/images/missing-icon.png");
            res2.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    // =========================================================================
    // SECTION 2: HTML STRUCTURAL SPECIFICATION & CONTROL IDS
    // =========================================================================

    [Fact]
    public async Task IndexHtml_ContainsAll11SidebarViews_AndNavigationRailButtons()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var html = await client.GetStringAsync("/");

            // 11 Navigation rail buttons with data-view attributes
            string[] expectedViews =
            [
                "tasks", "sessions", "history", "workspaces", "files",
                "skills", "subagents", "cron", "memory", "integrations", "settings"
            ];

            foreach (var view in expectedViews)
            {
                html.Should().Contain($"data-view=\"{view}\"", $"Sidebar rail must contain navigation button for '{view}'");
                html.Should().Contain($"id=\"view-{view}\"", $"Sidebar views must contain panel container 'id=\"view-{view}\"'");
            }
        }
    }

    [Fact]
    public async Task IndexHtml_ContainsAllRequiredInteractiveControlIds_MandatedBySpecification()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var html = await client.GetStringAsync("/");

            // Header & Global Controls
            html.Should().Contain("id=\"btn-palette-trigger\"");
            html.Should().Contain("id=\"header-session-select\"");
            html.Should().Contain("id=\"btn-theme-toggle\"");
            html.Should().Contain("id=\"btn-panic-stop\"");
            html.Should().Contain("id=\"ws-status-badge\"");

            // Sidebar Tasks View Controls
            html.Should().Contain("id=\"form-new-task\"");
            html.Should().Contain("id=\"task-prompt-input\"");
            html.Should().Contain("id=\"task-model-select\"");
            html.Should().Contain("id=\"task-autonomy-select\"");
            html.Should().Contain("id=\"btn-dispatch-task\"");
            html.Should().Contain("id=\"active-tasks-list\"");
            html.Should().Contain("id=\"task-history-list\"");

            // Sidebar Sessions View Controls
            html.Should().Contain("id=\"form-new-session\"");
            html.Should().Contain("id=\"session-title-input\"");
            html.Should().Contain("id=\"session-model-select\"");
            html.Should().Contain("id=\"sessions-list\"");

            // Sidebar Workspaces, Files, Skills, Subagents, Cron, Memory, Integrations, Settings
            html.Should().Contain("id=\"form-switch-workspace\"");
            html.Should().Contain("id=\"workspace-current-path\"");
            html.Should().Contain("id=\"btn-fs-refresh\"");
            html.Should().Contain("id=\"fs-tree-container\"");
            html.Should().Contain("id=\"form-install-skill\"");
            html.Should().Contain("id=\"installed-skills-list\"");
            html.Should().Contain("id=\"form-new-kanban\"");
            html.Should().Contain("id=\"kanban-col-pending\"");
            html.Should().Contain("id=\"kanban-col-inprogress\"");
            html.Should().Contain("id=\"kanban-col-blocked\"");
            html.Should().Contain("id=\"kanban-col-done\"");
            html.Should().Contain("id=\"form-new-cron\"");
            html.Should().Contain("id=\"cron-jobs-list\"");
            html.Should().Contain("id=\"memory-search-input\"");
            html.Should().Contain("id=\"form-memory-append\"");
            html.Should().Contain("id=\"integrations-list\"");
            html.Should().Contain("id=\"form-test-integration\"");
            html.Should().Contain("id=\"form-settings\"");
            html.Should().Contain("id=\"btn-save-settings\"");

            // Main Workspace Tabs & Tab Subcomponents
            html.Should().Contain("id=\"tab-chat\"");
            html.Should().Contain("id=\"tab-editor\"");
            html.Should().Contain("id=\"tab-terminal\"");
            html.Should().Contain("id=\"tab-diff\"");
            html.Should().Contain("id=\"tab-workflow\"");

            // Diff View Controls
            html.Should().Contain("id=\"diff-path-input\"");
            html.Should().Contain("id=\"btn-diff-load\"");
            html.Should().Contain("id=\"btn-diff-approve\"");
            html.Should().Contain("id=\"btn-diff-reject\"");
            html.Should().Contain("id=\"diff-proposed-textarea\"");
            html.Should().Contain("id=\"diff-viewer-container\"");

            // Code Editor Controls
            html.Should().Contain("id=\"editor-active-file\"");
            html.Should().Contain("id=\"btn-editor-load\"");
            html.Should().Contain("id=\"btn-editor-save\"");
            html.Should().Contain("id=\"code-editor-textarea\"");

            // Terminal Controls
            html.Should().Contain("id=\"terminal-backend-select\"");
            html.Should().Contain("id=\"terminal-console-output\"");
            html.Should().Contain("id=\"terminal-cmd-input\"");
            html.Should().Contain("id=\"btn-terminal-run\"");

            // Splitters
            html.Should().Contain("id=\"sidebar-splitter\"");
            html.Should().Contain("id=\"right-panel-splitter\"");

            // Modals
            html.Should().Contain("id=\"palette-modal-backdrop\"");
            html.Should().Contain("id=\"palette-search-input\"");
            html.Should().Contain("id=\"palette-results-list\"");
            html.Should().Contain("id=\"smart-approval-modal\"");
            html.Should().Contain("id=\"approval-yellow-banner\"");
            html.Should().Contain("id=\"approval-red-banner\"");
            html.Should().Contain("id=\"btn-approval-cancel\"");
            html.Should().Contain("id=\"btn-approval-reject\"");
            html.Should().Contain("id=\"btn-approval-accept\"");
        }
    }

    [Fact]
    public async Task IndexHtml_ContainsAll8RightPanelTelemetryModules()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var html = await client.GetStringAsync("/");

            string[] expectedModules =
            [
                "module-task-details",
                "module-agent-status",
                "module-active-tools",
                "module-modified-files",
                "module-execution-plan",
                "module-subagent-stream",
                "module-token-telemetry",
                "module-runtime-logs"
            ];

            foreach (var mod in expectedModules)
            {
                html.Should().Contain($"id=\"{mod}\"", $"Right panel must contain telemetry module '{mod}'");
            }
        }
    }

    // =========================================================================
    // SECTION 3: JAVASCRIPT FETCH WIRING & PROTOCOL VALIDATION
    // =========================================================================

    [Fact]
    public async Task AppJs_WiresRealFetchCalls_ToAllMandatedBackendEndpoints()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var js = await client.GetStringAsync("/app.js");

            // Verify REST Endpoint Paths Wired in fetch/api calls
            string[] requiredEndpoints =
            [
                "/api/tasks",
                "/api/sessions",
                "/api/fs/diff/approve",
                "/api/fs/diff/reject",
                "/api/terminal/exec",
                "/api/settings",
                "/api/status",
                "/api/fs/tree",
                "/api/fs/read",
                "/api/fs/write",
                "/api/fs/diff",
                "/api/skills",
                "/api/skills/install",
                "/api/kanban",
                "/api/cron",
                "/api/memory",
                "/api/integrations",
                "/api/panic-stop",
                "/api/chat"
            ];

            foreach (var endpoint in requiredEndpoints)
            {
                js.Should().Contain(endpoint, $"app.js must wire real network calls to backend endpoint '{endpoint}'");
            }
        }
    }

    [Fact]
    public async Task AppJs_HandlesAllWebSocketProtocolFrames_AndRegistersHeartbeat()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var js = await client.GetStringAsync("/app.js");

            string[] protocolFrames =
            [
                "stream_chunk",
                "tool_started",
                "tool_progress",
                "tool_completed",
                "tool_failed",
                "reasoning",
                "task_progress",
                "token_telemetry",
                "smart_approval_prompt",
                "terminal_output",
                "ghost_frame",
                "ghost_complete",
                "pong"
            ];

            foreach (var frame in protocolFrames)
            {
                js.Should().Contain($"'{frame}'", $"app.js WebSocket dispatcher must handle protocol frame '{frame}'");
            }

            js.Should().Contain("setInterval", "app.js should maintain WebSocket heartbeat ping interval");
            js.Should().Contain("{\"type\":\"ping\"}", "app.js should send heartbeat ping frames");
        }
    }

    [Fact]
    public async Task AppJs_ContainsHtmlSanitizer_ToPreventXssInStreamingContent()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var js = await client.GetStringAsync("/app.js");

            js.Should().Contain("function escapeHtml", "app.js must define an HTML escaping utility to prevent XSS");
            js.Should().Contain("&amp;");
            js.Should().Contain("&lt;");
            js.Should().Contain("&gt;");
        }
    }

    // =========================================================================
    // SECTION 4: SIMULATED UI END-TO-END WORKFLOWS OVER KESTREL
    // =========================================================================

    [Fact]
    public async Task SimulateUi_TaskDispatch_Pause_Resume_Cancel_FullLifecycle()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            // 1. Simulate UI user submitting #form-new-task
            var dispatchReq = new DispatchTaskRequest(
                Prompt: "Perform empirical stress verification on agent platform",
                Model: "gemini-2.5-flash"
            );

            var dispatchRes = await client.PostAsJsonAsync("/api/tasks", dispatchReq);
            dispatchRes.StatusCode.Should().Be(HttpStatusCode.OK);

            var taskDetail = await dispatchRes.Content.ReadFromJsonAsync<TaskDetailRecord>();
            taskDetail.Should().NotBeNull();
            taskDetail!.Id.Should().StartWith("task_");
            taskDetail.Prompt.Should().Be(dispatchReq.Prompt);

            var taskId = taskDetail.Id;

            // 2. Simulate UI polling or fetching task details
            var detailRes = await client.GetAsync($"/api/tasks/{taskId}");
            detailRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var retrievedDetail = await detailRes.Content.ReadFromJsonAsync<TaskDetailRecord>();
            retrievedDetail!.Id.Should().Be(taskId);

            // 3. Simulate UI user clicking Pause button on active task card
            var pauseRes = await client.PostAsync($"/api/tasks/{taskId}/pause", null);
            pauseRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 4. Simulate UI user clicking Resume button on active task card
            var resumeRes = await client.PostAsync($"/api/tasks/{taskId}/resume", null);
            resumeRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 5. Simulate UI user clicking Cancel button on active task card
            var cancelRes = await client.PostAsync($"/api/tasks/{taskId}/cancel", null);
            cancelRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 6. Query all tasks to ensure task is present in list for #active-tasks-list / #task-history-list
            var allTasks = await client.GetFromJsonAsync<TaskSummaryRecord[]>("/api/tasks");
            allTasks.Should().NotBeNull();
            allTasks!.Should().Contain(t => t.Id == taskId);
        }
    }

    [Fact]
    public async Task SimulateUi_FileDiffInspection_RejectThenApprove_Flow()
    {
        var (host, client, workDir, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var relativePath = "src/service-config.json";
            var fullPath = Path.Combine(workDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            const string initialContent = "{\n  \"mode\": \"debug\",\n  \"threads\": 2\n}\n";
            const string proposedContent = "{\n  \"mode\": \"production\",\n  \"threads\": 16\n}\n";

            await File.WriteAllTextAsync(fullPath, initialContent);

            // 1. Simulate UI user entering path and proposed text, then clicking #btn-diff-load
            var diffRes = await client.PostAsJsonAsync("/api/fs/diff", new FsDiffRequest(
                Path: relativePath,
                ProposedContent: proposedContent
            ));
            diffRes.StatusCode.Should().Be(HttpStatusCode.OK);

            var diff = await diffRes.Content.ReadFromJsonAsync<FsDiffResponse>();
            diff.Should().NotBeNull();
            diff!.HasChanges.Should().BeTrue();
            diff.AdditionsCount.Should().BeGreaterThan(0);
            diff.DeletionsCount.Should().BeGreaterThan(0);
            diff.UnifiedDiff.Should().Contain("+  \"mode\": \"production\"");
            diff.UnifiedDiff.Should().Contain("-  \"mode\": \"debug\"");

            // 2. Simulate UI user clicking #btn-diff-reject
            var rejectRes = await client.PostAsJsonAsync("/api/fs/diff/reject", new FsDiffRejectRequest(relativePath));
            rejectRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // Verify file on disk is unchanged after rejection
            var diskContentAfterReject = await File.ReadAllTextAsync(fullPath);
            diskContentAfterReject.Should().Be(initialContent, "File content on disk must remain unchanged when diff is rejected");

            // 3. Simulate UI user clicking #btn-diff-approve
            var approveRes = await client.PostAsJsonAsync("/api/fs/diff/approve", new FsDiffApproveRequest(
                Path: relativePath,
                ProposedContent: proposedContent
            ));
            approveRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // Verify file on disk has been updated after approval
            var diskContentAfterApprove = await File.ReadAllTextAsync(fullPath);
            diskContentAfterApprove.Should().Be(proposedContent, "File content on disk must be updated when diff is approved");

            // 4. Simulate UI editor opening file via GET /api/fs/read
            var readRes = await client.GetAsync($"/api/fs/read?path={Uri.EscapeDataString(relativePath)}");
            readRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var readPayload = await readRes.Content.ReadFromJsonAsync<FsReadResponse>();
            readPayload.Should().NotBeNull();
            readPayload!.Content.Should().Be(proposedContent);
        }
    }

    [Fact]
    public async Task SimulateUi_TerminalExecution_ReturnsStdoutAndDuration()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            // Simulate UI submitting command from #form-terminal
            var req = new TerminalExecRequest(
                Command: "echo \"Milestone 7 empirical verification\"",
                WorkingDir: null,
                TimeoutSeconds: 15
            );

            var res = await client.PostAsJsonAsync("/api/terminal/exec", req);
            res.StatusCode.Should().Be(HttpStatusCode.OK);

            var termRes = await res.Content.ReadFromJsonAsync<TerminalExecResponse>();
            termRes.Should().NotBeNull();
            termRes!.ExitCode.Should().Be(0);
            termRes.Stdout.Should().Contain("Milestone 7 empirical verification");
            termRes.ExecutionTimeMs.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task SimulateUi_SettingsConfiguration_SaveAndPersistence()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            // 1. Initial settings query
            var getRes1 = await client.GetAsync("/api/settings");
            getRes1.StatusCode.Should().Be(HttpStatusCode.OK);
            var initialSettings = await getRes1.Content.ReadFromJsonAsync<DesktopSettings>();
            initialSettings.Should().NotBeNull();

            // 2. Simulate UI user modifying settings form and clicking #btn-save-settings
            var updated = initialSettings!;
            updated.Models.DefaultModel = "claude-3-7-sonnet";
            updated.Models.FallbackModel = "ollama/llama3";
            updated.Permissions.DefaultAutonomyTier = "FullAutonomy";
            updated.Permissions.RequireApprovalForRed = true;

            var postRes = await client.PostAsJsonAsync("/api/settings", updated);
            postRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3. Query again to verify persistence
            var getRes2 = await client.GetAsync("/api/settings");
            getRes2.StatusCode.Should().Be(HttpStatusCode.OK);
            var persistedSettings = await getRes2.Content.ReadFromJsonAsync<DesktopSettings>();
            persistedSettings.Should().NotBeNull();
            persistedSettings!.Models.DefaultModel.Should().Be("claude-3-7-sonnet");
            persistedSettings.Models.FallbackModel.Should().Be("ollama/llama3");
            persistedSettings.Permissions.DefaultAutonomyTier.Should().Be("FullAutonomy");
            persistedSettings.Permissions.RequireApprovalForRed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task SimulateUi_SessionLifecycle_CreateActivateDelete()
    {
        var (host, client, workDir, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            // 1. Simulate UI user submitting #form-new-session
            var createReq = new CreateSessionApiRequest(
                Title: "Challenger Verification Session",
                Model: "gemini-2.5-flash",
                WorkingDir: workDir
            );

            var createRes = await client.PostAsJsonAsync("/api/sessions", createReq);
            createRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var created = await createRes.Content.ReadFromJsonAsync<JsonElement>();
            string sessionId = created.GetProperty("id").GetString()!;

            // 2. Simulate UI selecting session in #header-session-select -> calls activate
            var activateRes = await client.PostAsync($"/api/sessions/{sessionId}/activate", null);
            activateRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3. Query sessions list -> check session exists and isActive is true
            var listRes = await client.GetAsync("/api/sessions");
            listRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var sessions = await listRes.Content.ReadFromJsonAsync<JsonElement>();
            var found = false;
            foreach (var item in sessions.EnumerateArray())
            {
                if (item.GetProperty("id").GetString() == sessionId)
                {
                    found = true;
                    item.GetProperty("isActive").GetBoolean().Should().BeTrue();
                }
            }
            found.Should().BeTrue();

            // 4. Simulate UI user clicking delete session
            var delRes = await client.DeleteAsync($"/api/sessions/{sessionId}");
            delRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 5. Query sessions list -> verify session is removed
            var listResAfter = await client.GetAsync("/api/sessions");
            var sessionsAfter = await listResAfter.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var item in sessionsAfter.EnumerateArray())
            {
                item.GetProperty("id").GetString().Should().NotBe(sessionId);
            }
        }
    }

    [Fact]
    public async Task SimulateUi_KanbanWorkflow_CreateAndMoveCard()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            // 1. Simulate UI user adding a new card in #form-new-kanban
            var addCardReq = new
            {
                title = "Inspect Desktop UI Static Assets",
                description = "Empirically verify index.html, style.css, and app.js"
            };

            var postRes = await client.PostAsJsonAsync("/api/kanban", addCardReq);
            postRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var card = await postRes.Content.ReadFromJsonAsync<JsonElement>();
            string cardId = card.GetProperty("id").GetString()!;

            // 2. Move card from Pending to InProgress
            var moveReq1 = new { cardId, column = "InProgress" };
            var moveRes1 = await client.PostAsJsonAsync("/api/kanban/move", moveReq1);
            moveRes1.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3. Move card from InProgress to Done
            var moveReq2 = new { cardId, column = "Done" };
            var moveRes2 = await client.PostAsJsonAsync("/api/kanban/move", moveReq2);
            moveRes2.StatusCode.Should().Be(HttpStatusCode.OK);

            // 4. Query Kanban board and verify card is in Done column
            var getRes = await client.GetAsync("/api/kanban");
            getRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var allCards = await getRes.Content.ReadFromJsonAsync<JsonElement>();
            var cardInDone = false;
            foreach (var c in allCards.EnumerateArray())
            {
                if (c.GetProperty("id").GetString() == cardId)
                {
                    c.GetProperty("column").GetString().Should().Be("Done");
                    cardInDone = true;
                }
            }
            cardInDone.Should().BeTrue();
        }
    }

    [Fact]
    public async Task SimulateUi_PanicKillSwitch_EmergencyCutOffReflectedInStatus()
    {
        var (host, client, _, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            // Initial status: not panic triggered
            var statusRes1 = await client.GetAsync("/api/status");
            var status1 = await statusRes1.Content.ReadFromJsonAsync<JsonElement>();
            status1.GetProperty("panicTriggered").GetBoolean().Should().BeFalse();

            // Simulate UI user clicking #btn-panic-stop
            var panicRes = await client.PostAsync("/api/panic-stop", null);
            panicRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // Query status again: panicTriggered must be true
            var statusRes2 = await client.GetAsync("/api/status");
            var status2 = await statusRes2.Content.ReadFromJsonAsync<JsonElement>();
            status2.GetProperty("panicTriggered").GetBoolean().Should().BeTrue();
        }
    }
}
