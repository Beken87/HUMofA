using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HUMofA.Desktop;
using Xunit;

namespace HUMofA.Tests;

public class DesktopUiIntegrationTests
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

    private static async Task<(DesktopHost Host, HttpClient Client, string WorkDir)> CreateTestServerAsync()
    {
        var tempWorkDir = Path.Combine(Path.GetTempPath(), "humofa-desktop-ui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkDir);
        var uiDir = ResolveUiDirectory();
        var host = new DesktopHost(workspaceDir: tempWorkDir, uiRootDir: uiDir);
        var baseUrl = await host.StartAsync(port: 0);
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (host, client, tempWorkDir);
    }

    [Fact]
    public async Task DesktopHost_ServesIndexHtml_WithAllRequiredComponents()
    {
        var (host, client, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var res = await client.GetAsync("/");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

            var html = await res.Content.ReadAsStringAsync();

            // 1. App Title & Brand
            html.Should().Contain("HUMofA");
            html.Should().Contain("Desktop Workspace");

            // 2. Top Header Bar Elements
            html.Should().Contain("header-session-select");
            html.Should().Contain("btn-palette-trigger");
            html.Should().Contain("Ctrl+K");
            html.Should().Contain("btn-theme-toggle");
            html.Should().Contain("btn-panic-stop");
            html.Should().Contain("ws-status-badge");

            // 3. Collapsible Left Sidebar with 11 Views
            html.Should().Contain("sidebar");
            html.Should().Contain("view-tasks");
            html.Should().Contain("view-sessions");
            html.Should().Contain("view-history");
            html.Should().Contain("view-workspaces");
            html.Should().Contain("view-files");
            html.Should().Contain("view-skills");
            html.Should().Contain("view-subagents");
            html.Should().Contain("view-cron");
            html.Should().Contain("view-memory");
            html.Should().Contain("view-integrations");
            html.Should().Contain("view-settings");

            // 4. Tasks view elements (form, active, history)
            html.Should().Contain("task-prompt-input");
            html.Should().Contain("task-model-select");
            html.Should().Contain("btn-dispatch-task");
            html.Should().Contain("active-tasks-list");
            html.Should().Contain("task-history-list");

            // 5. Sessions view elements
            html.Should().Contain("sessions-list");
            html.Should().Contain("form-new-session");

            // 6. Adaptive Main Workspace & Tabbed Navigation
            html.Should().Contain("main-workspace");
            html.Should().Contain("workspace-tabs");
            html.Should().Contain("tab-chat");
            html.Should().Contain("tab-editor");
            html.Should().Contain("tab-terminal");
            html.Should().Contain("tab-diff");
            html.Should().Contain("tab-workflow");

            // 7. Chat Stream, Editor, Terminal, Diff
            html.Should().Contain("chat-stream-container");
            html.Should().Contain("chat-prompt-input");
            html.Should().Contain("code-editor-textarea");
            html.Should().Contain("terminal-console-output");
            html.Should().Contain("terminal-cmd-input");
            html.Should().Contain("diff-viewer-container");
            html.Should().Contain("btn-diff-approve");
            html.Should().Contain("btn-diff-reject");

            // 8. Resizable Splitters
            html.Should().Contain("sidebar-splitter");
            html.Should().Contain("right-panel-splitter");

            // 9. Contextual Right Panel with 8 Telemetry Modules
            html.Should().Contain("right-panel");
            html.Should().Contain("module-task-details");
            html.Should().Contain("module-agent-status");
            html.Should().Contain("module-active-tools");
            html.Should().Contain("module-modified-files");
            html.Should().Contain("module-execution-plan");
            html.Should().Contain("module-subagent-stream");
            html.Should().Contain("module-token-telemetry");
            html.Should().Contain("module-runtime-logs");

            // 10. Command Palette Modal & SmartApproval Modal
            html.Should().Contain("palette-modal-backdrop");
            html.Should().Contain("palette-search-input");
            html.Should().Contain("smart-approval-modal");
            html.Should().Contain("approval-yellow-banner");
            html.Should().Contain("approval-red-banner");
            html.Should().Contain("btn-approval-accept");
            html.Should().Contain("btn-approval-reject");
        }
    }

    [Fact]
    public async Task DesktopHost_ServesStyleCss_WithFullDesignSystemTokensAndDiffStyles()
    {
        var (host, client, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var res = await client.GetAsync("/style.css");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Content.Headers.ContentType?.MediaType.Should().Be("text/css");

            var css = await res.Content.ReadAsStringAsync();

            // 1. Dark Theme Tokens
            css.Should().Contain("--bg-primary");
            css.Should().Contain("--bg-secondary");
            css.Should().Contain("--accent-primary");
            css.Should().Contain("--border-strong");
            css.Should().Contain("--text-primary");

            // 2. Light Theme Tokens
            css.Should().Contain("[data-theme=\"light\"]");

            // 3. Diff Viewer Styling
            css.Should().Contain(".diff-line.add");
            css.Should().Contain(".diff-line.del");
            css.Should().Contain(".diff-line.hunk");
            css.Should().Contain("--diff-add-bg");
            css.Should().Contain("--diff-del-bg");

            // 4. Splitter & Resize handles
            css.Should().Contain(".resize-handle");
            css.Should().Contain("col-resize");

            // 5. Command Palette Modal
            css.Should().Contain(".command-palette-card");
            css.Should().Contain(".modal-backdrop");
        }
    }

    [Fact]
    public async Task DesktopHost_ServesAppJs_WithFullRealEndpointBindings()
    {
        var (host, client, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var res = await client.GetAsync("/app.js");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Content.Headers.ContentType?.MediaType.Should().Be("application/javascript");

            var js = await res.Content.ReadAsStringAsync();

            // 1. WebSocket Protocol
            js.Should().Contain("/ws/live");
            js.Should().Contain("stream_chunk");
            js.Should().Contain("tool_started");
            js.Should().Contain("tool_completed");
            js.Should().Contain("tool_failed");
            js.Should().Contain("reasoning");
            js.Should().Contain("task_progress");
            js.Should().Contain("token_telemetry");
            js.Should().Contain("smart_approval_prompt");
            js.Should().Contain("terminal_output");

            // 2. Sessions API Bindings
            js.Should().Contain("/api/sessions");

            // 3. Tasks API Bindings
            js.Should().Contain("/api/tasks");
            js.Should().Contain("/pause");
            js.Should().Contain("/resume");
            js.Should().Contain("/cancel");

            // 4. File System & Diff API Bindings
            js.Should().Contain("/api/fs/tree");
            js.Should().Contain("/api/fs/read");
            js.Should().Contain("/api/fs/write");
            js.Should().Contain("/api/fs/diff");
            js.Should().Contain("/api/fs/diff/approve");
            js.Should().Contain("/api/fs/diff/reject");

            // 5. Terminal API Bindings
            js.Should().Contain("/api/terminal/exec");

            // 6. Skills API Bindings
            js.Should().Contain("/api/skills");
            js.Should().Contain("/api/skills/install");

            // 7. Kanban, Cron, Memory, Integrations, Settings
            js.Should().Contain("/api/kanban");
            js.Should().Contain("/api/cron");
            js.Should().Contain("/api/memory");
            js.Should().Contain("/api/integrations");
            js.Should().Contain("/api/settings");
            js.Should().Contain("/api/panic-stop");

            // 8. Global Shortcut & Raycast Command Palette Listener
            js.Should().Contain("keydown");
            js.Should().Contain("humofa_theme");
            js.Should().Contain("humofa_sidebar_width");
        }
    }

    [Fact]
    public async Task DesktopHost_EndToEnd_TaskLifecycle_DispatchesAndTracks()
    {
        var (host, client, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            // 1. Dispatch autonomous task
            var dispatchRes = await client.PostAsJsonAsync("/api/tasks", new DispatchTaskRequest(
                Prompt: "Compile production release artifacts",
                Model: "gemini-2.5-flash"
            ));
            dispatchRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var taskDetail = await dispatchRes.Content.ReadFromJsonAsync<TaskDetailRecord>();
            taskDetail.Should().NotBeNull();
            taskDetail!.Id.Should().StartWith("task_");
            taskDetail.Status.Should().BeOneOf("Running", "Completed");

            // 2. Query task details
            var getRes = await client.GetAsync($"/api/tasks/{taskDetail.Id}");
            getRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var retrieved = await getRes.Content.ReadFromJsonAsync<TaskDetailRecord>();
            retrieved!.Id.Should().Be(taskDetail.Id);
            retrieved.Prompt.Should().Be("Compile production release artifacts");

            // 3. Query all tasks
            var allTasks = await client.GetFromJsonAsync<TaskSummaryRecord[]>("/api/tasks");
            allTasks.Should().NotBeNull();
            allTasks!.Should().Contain(t => t.Id == taskDetail.Id);
        }
    }

    [Fact]
    public async Task DesktopHost_EndToEnd_DiffApproval_ModifiesFileOnDisk()
    {
        var (host, client, workDir) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var testFilePath = "config.json";
            var fullTestPath = Path.Combine(workDir, testFilePath);
            await File.WriteAllTextAsync(fullTestPath, "{\"version\": 1}");

            // 1. Compute diff
            var diffRes = await client.PostAsJsonAsync("/api/fs/diff", new FsDiffRequest(
                Path: testFilePath,
                ProposedContent: "{\"version\": 2}"
            ));
            diffRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var diff = await diffRes.Content.ReadFromJsonAsync<FsDiffResponse>();
            diff!.HasChanges.Should().BeTrue();
            diff.AdditionsCount.Should().BeGreaterThan(0);

            // 2. Approve diff
            var approveRes = await client.PostAsJsonAsync("/api/fs/diff/approve", new FsDiffApproveRequest(
                Path: testFilePath,
                ProposedContent: "{\"version\": 2}"
            ));
            approveRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3. Verify file on disk has updated content
            var diskContent = await File.ReadAllTextAsync(fullTestPath);
            diskContent.Should().Be("{\"version\": 2}");
        }
    }

    [Fact]
    public async Task DesktopHost_EndToEnd_WebSocket_ConnectsAndRespondsToPing()
    {
        var (host, client, _) = await CreateTestServerAsync();
        await using (host)
        using (client)
        {
            var wsUri = new Uri(host.BaseUrl.Replace("http://", "ws://") + "/ws/live");
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await ws.ConnectAsync(wsUri, cts.Token);
            ws.State.Should().Be(WebSocketState.Open);

            // Send ping
            var pingBytes = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
            await ws.SendAsync(pingBytes, WebSocketMessageType.Text, true, cts.Token);

            // Receive pong
            var buffer = new byte[1024];
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            result.MessageType.Should().Be(WebSocketMessageType.Text);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            text.Should().Contain("pong");

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token);
        }
    }
}
