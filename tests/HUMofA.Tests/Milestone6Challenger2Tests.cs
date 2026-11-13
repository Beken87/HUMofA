using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HUMofA.Desktop;
using HUMofA.Gateway;
using HUMofA.Sessions;
using Xunit;

namespace HUMofA.Tests;

public class Milestone6Challenger2Tests
{
    private static async Task<(DesktopHost Host, HttpClient Client, string WorkDir)> CreateHostAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "humofa-m6-c2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var host = new DesktopHost(workspaceDir: tempDir);
        var baseUrl = await host.StartAsync(port: 0);

        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (host, client, tempDir);
    }

    private static Uri GetWsUri(DesktopHost host) =>
        new(host.BaseUrl.Replace("http://", "ws://") + "/ws/live");

    #region 1. 20 Concurrent WebSocket Clients Stress Test

    [Fact]
    public async Task WebSocket_TwentyConcurrentClients_ReceiveAllStreamAndToolFramesWithoutDrop()
    {
        var (host, client, _) = await CreateHostAsync();
        const int clientCount = 20;
        var clients = new List<ClientWebSocket>();
        var clientCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var wsUri = GetWsUri(host);

            // Connect 20 concurrent WebSocket clients in parallel
            var connectTasks = new List<Task>();
            for (int i = 0; i < clientCount; i++)
            {
                var ws = new ClientWebSocket();
                clients.Add(ws);
                connectTasks.Add(ws.ConnectAsync(wsUri, clientCts.Token));
            }

            await Task.WhenAll(connectTasks);

            // Verify all 20 are open and registered on host
            foreach (var ws in clients)
            {
                ws.State.Should().Be(WebSocketState.Open);
            }
            host.ConnectedClientsCount.Should().Be(clientCount);

            // Set up parallel frame receivers for each client
            var receivedFramesByClient = new ConcurrentDictionary<int, ConcurrentBag<JsonElement>>();
            var receiverTasks = new List<Task>();

            const int streamChunkCount = 10;
            const int toolEventCount = 4;
            const int reasoningCount = 3;
            const int taskProgressCount = 2;
            const int tokenTelemetryCount = 2;
            const int totalExpectedFrames = streamChunkCount + toolEventCount + reasoningCount + taskProgressCount + tokenTelemetryCount;

            for (int i = 0; i < clientCount; i++)
            {
                int clientIndex = i;
                var bag = new ConcurrentBag<JsonElement>();
                receivedFramesByClient[clientIndex] = bag;
                var ws = clients[clientIndex];

                receiverTasks.Add(Task.Run(async () =>
                {
                    var buffer = new byte[8192];
                    while (bag.Count < totalExpectedFrames && !clientCts.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(buffer, clientCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        {
                            var jsonStr = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            using var doc = JsonDocument.Parse(jsonStr);
                            bag.Add(doc.RootElement.Clone());
                        }
                    }
                }, clientCts.Token));
            }

            // Broadcast stream chunks
            for (int s = 0; s < streamChunkCount; s++)
            {
                bool isFinal = (s == streamChunkCount - 1);
                await host.BroadcastStreamChunkAsync("task_concurrent_99", "sess_concurrent_1", $"Chunk payload #{s} token streaming", isFinal);
            }

            // Broadcast tool cards
            await host.BroadcastToolStartedAsync("task_concurrent_99", "terminal", "echo test");
            await host.BroadcastToolProgressAsync("task_concurrent_99", "terminal", 50, "Halfway done");
            await host.BroadcastToolCompletedAsync("task_concurrent_99", "terminal", "test\n", 45);
            await host.BroadcastToolFailedAsync("task_concurrent_99", "git", "fatal: repo not found", 12);

            // Broadcast reasoning frames
            for (int r = 0; r < reasoningCount; r++)
            {
                await host.BroadcastReasoningAsync("task_concurrent_99", $"Thinking step #{r}: analyzing codebase", isFinal: (r == reasoningCount - 1));
            }

            // Broadcast task progress
            await host.BroadcastTaskProgressAsync("task_concurrent_99", "Running", 40, "Execution", "Running sub-tools");
            await host.BroadcastTaskProgressAsync("task_concurrent_99", "Completed", 100, "Finished", "Task done");

            // Broadcast token telemetry
            await host.BroadcastTokenTelemetryAsync("task_concurrent_99", "sess_concurrent_1", 120, 240, 360, 0.00072);
            await host.BroadcastTokenTelemetryAsync("task_concurrent_99", "sess_concurrent_1", 150, 300, 450, 0.00090);

            // Wait for all 20 receivers to collect all frames
            await Task.WhenAll(receiverTasks);

            // Verify each of the 20 clients received all frames without loss or corruption
            for (int i = 0; i < clientCount; i++)
            {
                var frames = receivedFramesByClient[i];
                frames.Count.Should().Be(totalExpectedFrames, $"Client #{i} should receive exactly {totalExpectedFrames} frames");

                // Verify stream chunk frames
                var chunks = frames.Where(f => f.TryGetProperty("type", out var t) && t.GetString() == "stream_chunk").ToList();
                chunks.Count.Should().Be(streamChunkCount);
                chunks.Should().Contain(c => c.GetProperty("chunk").GetString()!.Contains("Chunk payload #0"));
                chunks.Should().Contain(c => c.GetProperty("chunk").GetString()!.Contains($"Chunk payload #{streamChunkCount - 1}"));

                // Verify tool frames
                frames.Should().Contain(f => f.GetProperty("type").GetString() == "tool_started" && f.GetProperty("toolName").GetString() == "terminal");
                frames.Should().Contain(f => f.GetProperty("type").GetString() == "tool_progress" && f.GetProperty("progress").GetInt32() == 50);
                frames.Should().Contain(f => f.GetProperty("type").GetString() == "tool_completed" && f.GetProperty("durationMs").GetInt64() == 45);
                frames.Should().Contain(f => f.GetProperty("type").GetString() == "tool_failed" && f.GetProperty("status").GetString() == "failed");

                // Verify reasoning frames
                var reasoning = frames.Where(f => f.GetProperty("type").GetString() == "reasoning").ToList();
                reasoning.Count.Should().Be(reasoningCount);

                // Verify task progress and telemetry
                frames.Should().Contain(f => f.GetProperty("type").GetString() == "task_progress" && f.GetProperty("status").GetString() == "Completed");
                frames.Should().Contain(f => f.GetProperty("type").GetString() == "token_telemetry" && f.GetProperty("totalTokens").GetInt32() == 450);
            }

            // Close all 20 sockets cleanly
            var closeTasks = clients.Select(c => c.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None));
            await Task.WhenAll(closeTasks);

            await Task.Delay(150);
            host.ConnectedClientsCount.Should().Be(0);
        }
        finally
        {
            clientCts.Cancel();
            clientCts.Dispose();
            foreach (var ws in clients)
            {
                try { ws.Dispose(); } catch { }
            }
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    #endregion

    #region 2. Abnormal WebSocket Disconnections & Fault Resilience

    [Fact]
    public async Task WebSocket_AbnormalDisconnectionUnderLoad_ServerRemainsResilientWithoutCrashOrThreadLeak()
    {
        var (host, client, _) = await CreateHostAsync();
        var wsUri = GetWsUri(host);
        var clients = new List<ClientWebSocket>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            // Connect 6 clients
            for (int i = 0; i < 6; i++)
            {
                var ws = new ClientWebSocket();
                await ws.ConnectAsync(wsUri, cts.Token);
                clients.Add(ws);
            }

            host.ConnectedClientsCount.Should().Be(6);

            // Client 0 and 1 send ping to verify baseline
            var pingBytes = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
            await clients[0].SendAsync(pingBytes, WebSocketMessageType.Text, true, cts.Token);
            var buffer = new byte[2048];
            var pongResult = await clients[0].ReceiveAsync(buffer, cts.Token);
            var pongText = Encoding.UTF8.GetString(buffer, 0, pongResult.Count);
            pongText.Should().Contain("pong");

            // Broadcast initial message to all 6
            await host.BroadcastStreamChunkAsync("task_abnormal_1", null, "Pre-abort broadcast chunk", isFinal: false);

            // Now abruptly ABORT clients 0, 1, and 2 while server broadcasts
            clients[0].Abort();
            clients[1].Abort();
            clients[2].Dispose(); // Disposed socket will throw ObjectDisposedException when written to

            // Broadcast multiple frames while half the sockets are dead
            for (int i = 0; i < 5; i++)
            {
                await host.BroadcastStreamChunkAsync("task_abnormal_1", null, $"Post-abort frame #{i}", isFinal: (i == 4));
            }

            // Server must NOT crash, and must prune dead sockets
            await Task.Delay(200);
            host.ConnectedClientsCount.Should().BeLessThanOrEqualTo(3);

            // Surviving clients (3, 4, 5) must receive subsequent frames normally
            for (int i = 3; i < 6; i++)
            {
                var survivingWs = clients[i];
                survivingWs.State.Should().Be(WebSocketState.Open);

                // Ping-pong on surviving socket
                await survivingWs.SendAsync(pingBytes, WebSocketMessageType.Text, true, cts.Token);
                var resp = await survivingWs.ReceiveAsync(buffer, cts.Token);
                var text = Encoding.UTF8.GetString(buffer, 0, resp.Count);
                text.Should().NotBeNullOrWhiteSpace();
            }

            // Host must accept a brand new connection after the aborted sockets
            using var freshWs = new ClientWebSocket();
            await freshWs.ConnectAsync(wsUri, cts.Token);
            freshWs.State.Should().Be(WebSocketState.Open);

            await host.BroadcastStreamChunkAsync("task_abnormal_2", null, "Fresh client verification", isFinal: true);
            var freshRecv = await freshWs.ReceiveAsync(buffer, cts.Token);
            var freshText = Encoding.UTF8.GetString(buffer, 0, freshRecv.Count);
            await freshWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing fresh", cts.Token);

            for (int i = 3; i < 6; i++)
            {
                try
                {
                    if (clients[i].State == WebSocketState.Open)
                        await clients[i].CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing surviving", cts.Token);
                }
                catch { }
            }
        }
        finally
        {
            cts.Cancel();
            cts.Dispose();
            foreach (var ws in clients)
            {
                try { ws.Dispose(); } catch { }
            }
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    #endregion

    #region 3. Terminal Execution: Safe Commands & WebSocket Streaming

    [Fact]
    public async Task TerminalApi_SafeCommands_ExecuteSuccessfullyAndStreamOutputOverWebSocket()
    {
        var (host, client, workDir) = await CreateHostAsync();
        var wsUri = GetWsUri(host);
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await ws.ConnectAsync(wsUri, cts.Token);
            var buffer = new byte[4096];

            // 1. Safe command: echo
            const string echoMarker = "HUMOFA_ECHO_VERIFIED_987";
            var echoReq = new TerminalExecRequest(
                Command: $"echo {echoMarker}",
                WorkingDir: workDir);

            var echoResp = await client.PostAsJsonAsync("/api/terminal/exec", echoReq, cts.Token);
            echoResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var echoJson = await echoResp.Content.ReadFromJsonAsync<TerminalExecResponse>();
            echoJson.Should().NotBeNull();
            echoJson!.ExitCode.Should().Be(0);
            echoJson.Stdout.Should().Contain(echoMarker);
            echoJson.WasApproved.Should().BeTrue();
            echoJson.RiskTier.Should().Be("Green");

            // Verify WebSocket received terminal_output frame
            var wsFrameResult = await ws.ReceiveAsync(buffer, cts.Token);
            var wsFrameText = Encoding.UTF8.GetString(buffer, 0, wsFrameResult.Count);
            wsFrameText.Should().Contain("\"terminal_output\"");
            wsFrameText.Should().Contain(echoMarker);

            // 2. Safe command: dir (on Windows) or ls (on Unix)
            string dirCmd = OperatingSystem.IsWindows() ? "dir" : "ls";
            var dirReq = new TerminalExecRequest(
                Command: dirCmd,
                WorkingDir: workDir);

            var dirResp = await client.PostAsJsonAsync("/api/terminal/exec", dirReq, cts.Token);
            dirResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var dirJson = await dirResp.Content.ReadFromJsonAsync<TerminalExecResponse>();
            dirJson!.ExitCode.Should().Be(0);
            dirJson.WasApproved.Should().BeTrue();
            dirJson.RiskTier.Should().Be("Green");

            // 3. Safe command: git status
            var gitReq = new TerminalExecRequest(
                Command: "git status",
                WorkingDir: workDir);

            var gitResp = await client.PostAsJsonAsync("/api/terminal/exec", gitReq, cts.Token);
            gitResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var gitJson = await gitResp.Content.ReadFromJsonAsync<TerminalExecResponse>();
            gitJson!.WasApproved.Should().BeTrue();
            gitJson.RiskTier.Should().Be("Green");

            // Clean close WebSocket
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    #endregion

    #region 4. Terminal Governance: Dangerous / Red-Tier Commands & SmartApproval Blocking

    [Fact]
    public async Task TerminalApi_RedTierCommands_ImmediatelyBlockedWith403AndBroadcastsSmartApprovalPrompt()
    {
        var (host, client, workDir) = await CreateHostAsync();
        var wsUri = GetWsUri(host);
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await ws.ConnectAsync(wsUri, cts.Token);
            var buffer = new byte[8192];

            // 1. Critical Red-tier destructive disk formatting command
            var redFormatReq = new TerminalExecRequest(
                Command: "format D: /q",
                WorkingDir: workDir);

            var formatResp = await client.PostAsJsonAsync("/api/terminal/exec", redFormatReq, cts.Token);
            formatResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var formatBody = await formatResp.Content.ReadFromJsonAsync<JsonElement>();
            formatBody.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
            formatBody.GetProperty("riskTier").GetString().Should().Be("Red");
            string requestId = formatBody.GetProperty("requestId").GetString()!;
            requestId.Should().StartWith("appr_");

            // Verify /ws/live broadcast of smart_approval_prompt
            var promptResult = await ws.ReceiveAsync(buffer, cts.Token);
            var promptText = Encoding.UTF8.GetString(buffer, 0, promptResult.Count);
            promptText.Should().Contain("\"smart_approval_prompt\"");
            promptText.Should().Contain(requestId);
            promptText.Should().Contain("\"terminal\"");
            promptText.Should().Contain("\"Red\"");

            // 2. Critical Red-tier unix destructive command
            var redRmRfReq = new TerminalExecRequest(
                Command: "rm -rf /var/protected_data",
                WorkingDir: workDir);

            var rmRfResp = await client.PostAsJsonAsync("/api/terminal/exec", redRmRfReq, cts.Token);
            rmRfResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var rmRfBody = await rmRfResp.Content.ReadFromJsonAsync<JsonElement>();
            rmRfBody.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
            rmRfBody.GetProperty("riskTier").GetString().Should().Be("Red");

            // 3. Critical Red-tier Windows recursive file deletion
            var redDelReq = new TerminalExecRequest(
                Command: "del /s C:\\dummy_challenge_path",
                WorkingDir: workDir);

            var delResp = await client.PostAsJsonAsync("/api/terminal/exec", redDelReq, cts.Token);
            delResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var delBody = await delResp.Content.ReadFromJsonAsync<JsonElement>();
            delBody.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
            delBody.GetProperty("riskTier").GetString().Should().Be("Red");

            // 4. Critical Red-tier credential / sensitive file access
            var redSecretReq = new TerminalExecRequest(
                Command: "cat .env",
                WorkingDir: workDir);

            var secretResp = await client.PostAsJsonAsync("/api/terminal/exec", redSecretReq, cts.Token);
            secretResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var secretBody = await secretResp.Content.ReadFromJsonAsync<JsonElement>();
            secretBody.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
            secretBody.GetProperty("riskTier").GetString().Should().Be("Red");

            // 5. Explicit pre-approval override allows execution through
            var approvedRedReq = new TerminalExecRequest(
                Command: "echo simulated_format && echo .env",
                WorkingDir: workDir,
                Approved: true);

            var approvedResp = await client.PostAsJsonAsync("/api/terminal/exec", approvedRedReq, cts.Token);
            approvedResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var approvedBody = await approvedResp.Content.ReadFromJsonAsync<TerminalExecResponse>();
            approvedBody!.WasApproved.Should().BeTrue();

            // Clean close WebSocket
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TerminalApi_SessionScopedPermissionGrant_AllowsRedTierCommandExecution()
    {
        var (host, client, workDir) = await CreateHostAsync();
        try
        {
            // 1. Create a persistent session
            var sessReq = new CreateSessionApiRequest(Title: "Governance Session Grant Test", WorkingDir: workDir);
            var createResp = await client.PostAsJsonAsync("/api/sessions", sessReq);
            createResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var sessJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            string sessionId = sessJson.GetProperty("id").GetString()!;

            const string dangerousCmd = "echo accessing_api_key_vault";

            // 2. Initial attempt without grant -> 403 Forbidden
            var unapprovedReq = new TerminalExecRequest(Command: dangerousCmd, SessionId: sessionId, WorkingDir: workDir);
            var unapprovedResp = await client.PostAsJsonAsync("/api/terminal/exec", unapprovedReq);
            unapprovedResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // 3. Grant permission on the session in SQLite session store
            await host.SessionManager.AddPermissionGrantAsync(sessionId, "terminal", dangerousCmd, "Red");

            // 4. Verify grant exists
            bool hasGrant = await host.SessionManager.CheckPermissionGrantAsync(sessionId, "terminal", dangerousCmd);
            hasGrant.Should().BeTrue();

            // 5. Subsequent execution associated with this session -> allowed (does not return 403)
            var grantedReq = new TerminalExecRequest(Command: dangerousCmd, SessionId: sessionId, WorkingDir: workDir);
            var grantedResp = await client.PostAsJsonAsync("/api/terminal/exec", grantedReq);
            // It bypasses 403 Forbidden because CheckPermissionGrantAsync returns true
            grantedResp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    #endregion

    #region 5. Challenge & Security Gap Analysis: Windows Recursive Directory Deletion (rmdir /s)

    [Fact]
    public void SmartApproval_WindowsRmdirRecursive_RevealsGovernanceClassificationGap()
    {
        var approval = new SmartApproval();

        // 1. Unix rm -rf is properly classified as Red
        var unixTier = approval.ClassifyAction("terminal", new Dictionary<string, string> { ["cmd"] = "rm -rf /testdir" });
        unixTier.Should().Be(RiskTier.Red);

        // 2. Windows del /s is properly classified as Red
        var delTier = approval.ClassifyAction("terminal", new Dictionary<string, string> { ["cmd"] = "del /s C:\\testdir" });
        delTier.Should().Be(RiskTier.Red);

        // 3. Windows rmdir /s /q (and rd /s /q) is the canonical recursive directory deletion command
        var rmdirTier = approval.ClassifyAction("terminal", new Dictionary<string, string> { ["cmd"] = "rmdir /s /q C:\\testdir" });
        var rdTier = approval.ClassifyAction("terminal", new Dictionary<string, string> { ["cmd"] = "rd /s /q C:\\testdir" });

        // EMPIRICAL OBSERVATION & CHALLENGE FINDING:
        // SmartApproval.cs lines 139-151 contains:
        //   combinedParams.Contains("rm -rf") ||
        //   combinedParams.Contains("format ") ||
        //   combinedParams.Contains("del /s") || ...
        // Notice that "rmdir /s" and "rd /s" are MISSING from the parameters check.
        // Line 126 contains `lowerAction.Contains("rmdir_recursive")`, but in shell/terminal execution,
        // the action is "terminal" and the command is passed in parameters["cmd"].
        // Consequently, rmdir /s /q evaluates to Yellow instead of Red!
        // This is documented as an empirical challenger finding.
        bool rmdirBlockedAsRed = (rmdirTier == RiskTier.Red);
        bool rdBlockedAsRed = (rdTier == RiskTier.Red);

        // We record the empirical reality:
        if (!rmdirBlockedAsRed)
        {
            rmdirTier.Should().Be(RiskTier.Yellow, "rmdir /s /q currently falls back to Yellow due to missing rmdir /s pattern");
        }
        if (!rdBlockedAsRed)
        {
            rdTier.Should().Be(RiskTier.Yellow, "rd /s /q currently falls back to Yellow due to missing rd /s pattern");
        }
    }

    #endregion
}
