using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HUMofA.Desktop;
using Xunit;

namespace HUMofA.Tests;

public class DesktopHostApiTests
{
    private static async Task<(DesktopHost Host, HttpClient Client, string WorkDir)> CreateTestHostAsync()
    {
        var tempWorkDir = Path.Combine(Path.GetTempPath(), "humofa-desktop-api-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkDir);

        var host = new DesktopHost(workspaceDir: tempWorkDir);
        var baseUrl = await host.StartAsync(port: 0);

        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (host, client, tempWorkDir);
    }

    [Fact]
    public async Task SessionsApi_CrudAndActivation_ShouldWorkEndToEnd()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // 1. Create Session
            var createReq = new CreateSessionApiRequest(
                Title: "Refactor Pipeline",
                Model: "gemini-2.5-pro",
                WorkingDir: workDir,
                Instructions: "You are an autonomous code refactoring assistant.",
                AutonomyTier: "Guarded",
                MaxTokens: 16384);

            var createResp = await client.PostAsJsonAsync("/api/sessions", createReq);
            createResp.StatusCode.Should().Be(HttpStatusCode.OK);

            var session = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            string sessionId = session.GetProperty("id").GetString()!;
            sessionId.Should().NotBeNullOrWhiteSpace();
            session.GetProperty("title").GetString().Should().Be("Refactor Pipeline");
            session.GetProperty("model").GetString().Should().Be("gemini-2.5-pro");

            // 2. List Sessions
            var listResp = await client.GetAsync("/api/sessions");
            listResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
            list.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

            // 3. Get Session Details
            var detailResp = await client.GetAsync($"/api/sessions/{sessionId}");
            detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
            detail.GetProperty("session").GetProperty("id").GetString().Should().Be(sessionId);
            var transcript = detail.GetProperty("transcript");
            transcript.GetArrayLength().Should().Be(1);
            transcript[0].GetProperty("role").GetString().Should().Be("system");
            transcript[0].GetProperty("content").GetString().Should().Contain("refactoring assistant");

            // 4. Update Session (PATCH)
            var patchReq = new UpdateSessionApiRequest(
                Title: "Refactor Pipeline v2",
                Model: "claude-3-7-sonnet",
                Pinned: true);
            var patchResp = await client.PatchAsJsonAsync($"/api/sessions/{sessionId}", patchReq);
            patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var updated = await patchResp.Content.ReadFromJsonAsync<JsonElement>();
            updated.GetProperty("title").GetString().Should().Be("Refactor Pipeline v2");
            updated.GetProperty("model").GetString().Should().Be("claude-3-7-sonnet");
            updated.GetProperty("pinned").GetBoolean().Should().BeTrue();

            // 5. Activate Session
            var activateResp = await client.PostAsync($"/api/sessions/{sessionId}/activate", null);
            activateResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var activateResult = await activateResp.Content.ReadFromJsonAsync<JsonElement>();
            activateResult.GetProperty("success").GetBoolean().Should().BeTrue();
            activateResult.GetProperty("activeSessionId").GetString().Should().Be(sessionId);

            // 6. Delete Session
            var deleteResp = await client.DeleteAsync($"/api/sessions/{sessionId}");
            deleteResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 7. Verify 404 after deletion
            var notFoundResp = await client.GetAsync($"/api/sessions/{sessionId}");
            notFoundResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TasksApi_DispatchAndExecution_ShouldStreamAndComplete()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            var dispatchReq = new DispatchTaskRequest(
                Prompt: "Analyze project layout and dependencies",
                Model: "gemini-2.5-flash",
                ToolPermissions: new List<string> { "terminal", "filesystem" });

            var dispatchResp = await client.PostAsJsonAsync("/api/tasks", dispatchReq);
            dispatchResp.StatusCode.Should().Be(HttpStatusCode.OK);

            var taskObj = await dispatchResp.Content.ReadFromJsonAsync<JsonElement>();
            string taskId = taskObj.GetProperty("id").GetString()!;
            taskId.Should().StartWith("task_");
            taskObj.GetProperty("steps").GetArrayLength().Should().Be(4);
            taskObj.GetProperty("executionPlan").GetArrayLength().Should().Be(4);

            // List tasks
            var listResp = await client.GetAsync("/api/tasks");
            listResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
            list.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

            // Poll for task completion
            JsonElement finalTask = default;
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                var pollResp = await client.GetAsync($"/api/tasks/{taskId}");
                pollResp.StatusCode.Should().Be(HttpStatusCode.OK);
                finalTask = await pollResp.Content.ReadFromJsonAsync<JsonElement>();
                string status = finalTask.GetProperty("status").GetString()!;
                if (status == "Completed" || status == "Failed")
                    break;
            }

            finalTask.GetProperty("status").GetString().Should().Be("Completed");
            finalTask.GetProperty("progressPercent").GetInt32().Should().Be(100);
            finalTask.GetProperty("tokensConsumed").GetInt32().Should().BeGreaterThan(0);
            finalTask.GetProperty("output").GetString().Should().NotBeNullOrWhiteSpace();
            finalTask.GetProperty("toolEvents").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TasksApi_PauseResumeAndCancel_ShouldManageExecutionState()
    {
        var (host, client, _) = await CreateTestHostAsync();
        try
        {
            var dispatchReq = new DispatchTaskRequest(
                Prompt: "Perform comprehensive deep security analysis of all endpoints",
                Model: "gemini-2.5-flash");

            var dispatchResp = await client.PostAsJsonAsync("/api/tasks", dispatchReq);
            var taskObj = await dispatchResp.Content.ReadFromJsonAsync<JsonElement>();
            string taskId = taskObj.GetProperty("id").GetString()!;

            // Wait briefly for task to start running
            for (int i = 0; i < 20; i++)
            {
                var poll = await client.GetAsync($"/api/tasks/{taskId}");
                var pollJson = await poll.Content.ReadFromJsonAsync<JsonElement>();
                if (pollJson.GetProperty("status").GetString() == "Running")
                    break;
                await Task.Delay(15);
            }

            // Pause task
            var pauseResp = await client.PostAsync($"/api/tasks/{taskId}/pause", null);
            pauseResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var pauseObj = await pauseResp.Content.ReadFromJsonAsync<JsonElement>();
            pauseObj.GetProperty("status").GetString().Should().Be("Paused");

            // Resume task
            var resumeResp = await client.PostAsync($"/api/tasks/{taskId}/resume", null);
            resumeResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var resumeObj = await resumeResp.Content.ReadFromJsonAsync<JsonElement>();
            resumeObj.GetProperty("status").GetString().Should().Be("Running");

            // Cancel task
            var cancelResp = await client.PostAsync($"/api/tasks/{taskId}/cancel", null);
            cancelResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var cancelObj = await cancelResp.Content.ReadFromJsonAsync<JsonElement>();
            cancelObj.GetProperty("status").GetString().Should().Be("Cancelled");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task FilesystemApi_TreeReadWriteAndDiff_ShouldWorkSafely()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // 1. Write file
            var writeReq = new FsWriteRequest(
                Path: "config/app_config.json",
                Content: "{\n  \"version\": \"1.0.0\",\n  \"env\": \"dev\"\n}",
                Overwrite: true,
                CreateDirectory: true);

            var writeResp = await client.PostAsJsonAsync("/api/fs/write", writeReq);
            writeResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var writeResult = await writeResp.Content.ReadFromJsonAsync<JsonElement>();
            writeResult.GetProperty("success").GetBoolean().Should().BeTrue();
            writeResult.GetProperty("bytesWritten").GetInt32().Should().BeGreaterThan(0);

            // 2. Tree enumeration
            var treeResp = await client.GetAsync("/api/fs/tree");
            treeResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var treeObj = await treeResp.Content.ReadFromJsonAsync<JsonElement>();
            treeObj.GetProperty("items").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

            // 3. Read file
            var readResp = await client.GetAsync("/api/fs/read?path=config/app_config.json");
            readResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var readObj = await readResp.Content.ReadFromJsonAsync<JsonElement>();
            readObj.GetProperty("content").GetString().Should().Contain("\"env\": \"dev\"");

            // 4. Compute Diff
            var proposed = "{\n  \"version\": \"1.1.0\",\n  \"env\": \"production\"\n}";
            var diffReq = new FsDiffRequest("config/app_config.json", proposed);
            var diffResp = await client.PostAsJsonAsync("/api/fs/diff", diffReq);
            diffResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var diffObj = await diffResp.Content.ReadFromJsonAsync<JsonElement>();
            diffObj.GetProperty("hasChanges").GetBoolean().Should().BeTrue();
            diffObj.GetProperty("additionsCount").GetInt32().Should().BeGreaterThan(0);
            diffObj.GetProperty("unifiedDiff").GetString().Should().Contain("+  \"env\": \"production\"");

            // 5. Query cached diff
            var getDiffResp = await client.GetAsync("/api/fs/diff?path=config/app_config.json");
            getDiffResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 6. Approve diff
            var approveReq = new FsDiffApproveRequest(Path: "config/app_config.json");
            var approveResp = await client.PostAsJsonAsync("/api/fs/diff/approve", approveReq);
            approveResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var approveObj = await approveResp.Content.ReadFromJsonAsync<JsonElement>();
            approveObj.GetProperty("success").GetBoolean().Should().BeTrue();
            approveObj.GetProperty("backupCreated").GetBoolean().Should().BeTrue();

            // 7. Verify file on disk has updated
            var reReadResp = await client.GetAsync("/api/fs/read?path=config/app_config.json");
            var reReadObj = await reReadResp.Content.ReadFromJsonAsync<JsonElement>();
            reReadObj.GetProperty("content").GetString().Should().Contain("\"env\": \"production\"");

            // 8. Reject diff test
            var rejectedProposed = "{\n  \"rejected\": true\n}";
            await client.PostAsJsonAsync("/api/fs/diff", new FsDiffRequest("config/app_config.json", rejectedProposed));
            var rejectResp = await client.PostAsJsonAsync("/api/fs/diff/reject", new FsDiffRejectRequest(Path: "config/app_config.json"));
            rejectResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // File should remain unchanged
            var verifyResp = await client.GetAsync("/api/fs/read?path=config/app_config.json");
            var verifyObj = await verifyResp.Content.ReadFromJsonAsync<JsonElement>();
            verifyObj.GetProperty("content").GetString().Should().Contain("\"env\": \"production\"");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AgentManagementApi_CreateUpdateAndToolPermissions_ShouldSucceed()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // 1. List initial agents
            var listResp = await client.GetAsync("/api/agents");
            listResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 2. Create Agent
            var createReq = new CreateAgentApiRequest(
                Name: "CodeAuditor",
                SystemPrompt: "You verify adherence to software engineering standards.",
                PreferredModel: "claude-3-7-sonnet",
                WorkingDirectory: workDir,
                ToolPermissions: new List<string> { "terminal", "filesystem", "git" },
                MaxTokenBudget: 65536,
                AutonomyTier: "Guarded");

            var createResp = await client.PostAsJsonAsync("/api/agents", createReq);
            createResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var agentObj = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            string agentId = agentObj.GetProperty("config").GetProperty("id").GetString()!;
            agentId.Should().StartWith("agent_");

            var perms = agentObj.GetProperty("enabledPermissions");
            perms.EnumerateArray().Select(e => e.GetString()).Should().Contain(new[] { "terminal", "filesystem", "git" });

            // 3. Get Agent by ID
            var getResp = await client.GetAsync($"/api/agents/{agentId}");
            getResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var getObj = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            getObj.GetProperty("config").GetProperty("name").GetString().Should().Be("CodeAuditor");

            // 4. Update Agent (PATCH)
            var patchReq = new UpdateAgentApiRequest(
                Name: "CodeAuditor Senior",
                PreferredModel: "gemini-2.5-pro",
                MaxTokenBudget: 131072);

            var patchResp = await client.PatchAsJsonAsync($"/api/agents/{agentId}", patchReq);
            patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var patchObj = await patchResp.Content.ReadFromJsonAsync<JsonElement>();
            patchObj.GetProperty("config").GetProperty("name").GetString().Should().Be("CodeAuditor Senior");
            patchObj.GetProperty("config").GetProperty("preferredModel").GetString().Should().Be("gemini-2.5-pro");
            patchObj.GetProperty("config").GetProperty("maxTokenBudget").GetInt32().Should().Be(131072);

            // 5. Toggle Tool Permissions
            var toggleReq = new AgentToolPermissionsToggleRequest(Permissions: new List<string> { "terminal", "cdp", "web" });
            var toggleResp = await client.PostAsJsonAsync($"/api/agents/{agentId}/tools/permissions", toggleReq);
            toggleResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var toggleObj = await toggleResp.Content.ReadFromJsonAsync<JsonElement>();
            var newPerms = toggleObj.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToList();
            newPerms.Should().Contain(new[] { "terminal", "cdp", "web" });

            // 6. Delete Agent
            var deleteResp = await client.DeleteAsync($"/api/agents/{agentId}");
            deleteResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 7. Verify 404
            var notFoundResp = await client.GetAsync($"/api/agents/{agentId}");
            notFoundResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SettingsApi_PersistenceAndMasking_ShouldWorkCleanly()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // 1. Get default settings
            var getResp = await client.GetAsync("/api/settings");
            getResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var settingsObj = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            settingsObj.GetProperty("appearance").GetProperty("theme").GetString().Should().Be("dark");

            // 2. Post updated settings
            var updateReq = new DesktopSettings
            {
                Appearance = new AppearanceSettings
                {
                    Theme = "light",
                    FontSize = 16,
                    FontFamily = "Consolas, monospace",
                    CompactSidebar = true
                },
                Models = new ModelSettings
                {
                    DefaultModel = "gemini-2.5-pro",
                    AvailableModels = new List<string> { "gemini-2.5-pro", "claude-3-7-sonnet" },
                    FallbackModel = "offline-mock"
                },
                ApiKeys = new ApiKeySettings
                {
                    GeminiApiKey = "AIzaSyD-TestKey1234567890",
                    OpenAiApiKey = "sk-proj-TestOpenAiKey9876543210"
                },
                Permissions = new PermissionSettings
                {
                    DefaultAutonomyTier = "Conservative",
                    AllowedTools = new List<string> { "terminal", "filesystem", "git" },
                    RequireApprovalForRed = true
                },
                DefaultWorkingDir = workDir,
                TelemetryEnabled = true
            };

            var postResp = await client.PostAsJsonAsync("/api/settings", updateReq);
            postResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var updatedMasked = await postResp.Content.ReadFromJsonAsync<JsonElement>();

            // Assert masked response
            updatedMasked.GetProperty("appearance").GetProperty("theme").GetString().Should().Be("light");
            updatedMasked.GetProperty("appearance").GetProperty("fontSize").GetInt32().Should().Be(16);
            updatedMasked.GetProperty("models").GetProperty("defaultModel").GetString().Should().Be("gemini-2.5-pro");

            string maskedGemini = updatedMasked.GetProperty("apiKeys").GetProperty("geminiApiKey").GetString()!;
            maskedGemini.Should().StartWith("AIz***");
            maskedGemini.Should().EndWith("7890");

            // 3. Verify on disk persistence with unmasked key
            var settingsFilePath = Path.Combine(workDir, "settings.json");
            File.Exists(settingsFilePath).Should().BeTrue();
            var diskJson = await File.ReadAllTextAsync(settingsFilePath);
            diskJson.Should().Contain("AIzaSyD-TestKey1234567890");

            // 4. Test validation failure on invalid theme
            var invalidReq = new DesktopSettings
            {
                Appearance = new AppearanceSettings { Theme = "invalid-theme-value" }
            };
            var invalidResp = await client.PostAsJsonAsync("/api/settings", invalidReq);
            invalidResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TerminalApi_ExecWithRiskEvaluationAndOutput_ShouldWork()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // Safe read-only command (Green tier)
            var execReq = new TerminalExecRequest(
                Command: "echo HUMofA_Terminal_Test_Output",
                WorkingDir: workDir);

            var execResp = await client.PostAsJsonAsync("/api/terminal/exec", execReq);
            execResp.StatusCode.Should().Be(HttpStatusCode.OK);

            var execObj = await execResp.Content.ReadFromJsonAsync<JsonElement>();
            execObj.GetProperty("exitCode").GetInt32().Should().Be(0);
            execObj.GetProperty("stdout").GetString().Should().Contain("HUMofA_Terminal_Test_Output");
            execObj.GetProperty("wasApproved").GetBoolean().Should().BeTrue();
            execObj.GetProperty("riskTier").GetString().Should().Be("Green");

            // Red tier command without approval -> 403 Forbidden
            var redReq = new TerminalExecRequest(
                Command: "format D: /q",
                WorkingDir: workDir);

            var redResp = await client.PostAsJsonAsync("/api/terminal/exec", redReq);
            redResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var redObj = await redResp.Content.ReadFromJsonAsync<JsonElement>();
            redObj.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
            redObj.GetProperty("riskTier").GetString().Should().Be("Red");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task IntegrationsApi_StatusAndTestPing_ShouldSucceed()
    {
        var (host, client, _) = await CreateTestHostAsync();
        try
        {
            // GET /api/integrations
            var getResp = await client.GetAsync("/api/integrations");
            getResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var channels = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            channels.GetArrayLength().Should().Be(7);
            var channelNames = channels.EnumerateArray().Select(c => c.GetProperty("channel").GetString()).ToList();
            channelNames.Should().Contain(new[] { "telegram", "discord", "slack", "whatsapp", "signal", "email", "cli" });

            // POST /api/integrations/test
            var testReq = new IntegrationTestRequest("cli", "Testing CLI integration bridge");
            var testResp = await client.PostAsJsonAsync("/api/integrations/test", testReq);
            testResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var testObj = await testResp.Content.ReadFromJsonAsync<JsonElement>();
            testObj.GetProperty("success").GetBoolean().Should().BeTrue();
            testObj.GetProperty("channel").GetString().Should().Be("cli");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WebSocket_LiveProtocol_BroadcastsAndHeartbeat()
    {
        var (host, client, _) = await CreateTestHostAsync();
        try
        {
            var wsUri = new Uri(host.BaseUrl.Replace("http://", "ws://") + "/ws/live");
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await ws.ConnectAsync(wsUri, cts.Token);
            ws.State.Should().Be(WebSocketState.Open);
            host.ConnectedClientsCount.Should().Be(1);

            // 1. Send Ping, receive Pong
            var pingBytes = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
            await ws.SendAsync(pingBytes, WebSocketMessageType.Text, true, cts.Token);

            var buffer = new byte[4096];
            var recvResult = await ws.ReceiveAsync(buffer, cts.Token);
            var pongJson = Encoding.UTF8.GetString(buffer, 0, recvResult.Count);
            pongJson.Should().Contain("\"pong\"");

            // 2. Server broadcast stream chunk
            await host.BroadcastStreamChunkAsync("task_123", "sess_456", "Hello streaming world", isFinal: false);

            var chunkResult = await ws.ReceiveAsync(buffer, cts.Token);
            var chunkJson = Encoding.UTF8.GetString(buffer, 0, chunkResult.Count);
            chunkJson.Should().Contain("\"stream_chunk\"");
            chunkJson.Should().Contain("Hello streaming world");

            // 3. Server broadcast tool event
            await host.BroadcastToolStartedAsync("task_123", "terminal", "ls -la");
            var toolResult = await ws.ReceiveAsync(buffer, cts.Token);
            var toolJson = Encoding.UTF8.GetString(buffer, 0, toolResult.Count);
            toolJson.Should().Contain("\"tool_started\"");
            toolJson.Should().Contain("terminal");

            // Close socket cleanly
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token);
            await Task.Delay(100);
            host.ConnectedClientsCount.Should().Be(0);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ErrorHandlingAndSessionToolRecording_ShouldWorkAsExpected()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // 1. Missing session / 404 tests
            var missingSessResp = await client.GetAsync("/api/sessions/nonexistent_session_id");
            missingSessResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var patchMissingResp = await client.PatchAsJsonAsync("/api/sessions/nonexistent_session_id", new UpdateSessionApiRequest(Title: "Title"));
            patchMissingResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var activateMissingResp = await client.PostAsync("/api/sessions/nonexistent_session_id/activate", null);
            activateMissingResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            // 2. Missing task / 404
            var missingTaskResp = await client.GetAsync("/api/tasks/nonexistent_task_id");
            missingTaskResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var pauseMissingResp = await client.PostAsync("/api/tasks/nonexistent_task_id/pause", null);
            pauseMissingResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            // 3. Filesystem 404 / 400
            var missingFileResp = await client.GetAsync("/api/fs/read?path=nonexistent_file_path.txt");
            missingFileResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var emptyPathWriteResp = await client.PostAsJsonAsync("/api/fs/write", new FsWriteRequest("", "data"));
            emptyPathWriteResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // 4. Agent 400 validation
            var invalidAgentResp = await client.PostAsJsonAsync("/api/agents", new CreateAgentApiRequest("", ""));
            invalidAgentResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // 5. Terminal execution associated with Session records tool call in transcript
            var createSessResp = await client.PostAsJsonAsync("/api/sessions", new CreateSessionApiRequest(Title: "Tool Test Session"));
            var sessJson = await createSessResp.Content.ReadFromJsonAsync<JsonElement>();
            string sessId = sessJson.GetProperty("id").GetString()!;

            var termResp = await client.PostAsJsonAsync("/api/terminal/exec", new TerminalExecRequest(
                Command: "echo RecordedSessionToolOutput",
                SessionId: sessId,
                WorkingDir: workDir));
            termResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // Verify tool call is present in session transcript
            var sessDetailResp = await client.GetAsync($"/api/sessions/{sessId}");
            var sessDetailJson = await sessDetailResp.Content.ReadFromJsonAsync<JsonElement>();
            var toolCalls = sessDetailJson.GetProperty("toolCalls");
            toolCalls.GetArrayLength().Should().Be(1);
            toolCalls[0].GetProperty("toolName").GetString().Should().Be("terminal");
            toolCalls[0].GetProperty("callArgs").GetString().Should().Contain("RecordedSessionToolOutput");
            toolCalls[0].GetProperty("status").GetString().Should().Be("success");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }
}
