using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HUMofA.Agent;
using HUMofA.Desktop;
using HUMofA.Sessions;
using Xunit;

namespace HUMofA.Tests;

/// <summary>
/// Empirical challenge test suite for Milestone 6: Desktop Host REST API & Backend Architecture.
/// Authored by challenger_m6_1.
/// Adversarially stress-tests:
/// 1. Sessions CRUD with high-concurrency parallel creations, activation switching, and transcript querying.
/// 2. Tasks state transitions: rapid concurrent dispatch, pause, resume, cancel, and telemetry completion.
/// 3. Filesystem path security: empirical boundary probing with relative traversal (../, ..\) and absolute paths.
/// 4. Settings persistence: runtime environment variable synchronization and disk persistence across host re-initialization.
/// </summary>
public class Milestone6Challenger1Tests
{
    private static async Task<(DesktopHost Host, HttpClient Client, string WorkDir)> CreateTestHostAsync(string? explicitWorkDir = null)
    {
        var tempWorkDir = explicitWorkDir ?? Path.Combine(Path.GetTempPath(), "humofa-m6-challenge1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkDir);

        var host = new DesktopHost(workspaceDir: tempWorkDir);
        var baseUrl = await host.StartAsync(port: 0);

        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (host, client, tempWorkDir);
    }

    // =========================================================================
    // SECTION 1: SESSIONS CRUD & CONCURRENCY STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task SessionsApi_ConcurrentCreation_30ParallelRequests_CreatesDistinctSessionsWithoutCorruption()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            const int concurrency = 30;
            var tasks = Enumerable.Range(0, concurrency).Select(async i =>
            {
                var req = new CreateSessionApiRequest(
                    Title: $"Stress Session #{i}",
                    Model: i % 2 == 0 ? "gemini-2.5-pro" : "claude-3-7-sonnet",
                    WorkingDir: workDir,
                    Instructions: $"System instructions for worker {i}",
                    AutonomyTier: i % 3 == 0 ? "Autonomous" : "Guarded",
                    MaxTokens: 8192 + (i * 100));

                var resp = await client.PostAsJsonAsync("/api/sessions", req);
                resp.StatusCode.Should().Be(HttpStatusCode.OK);

                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                string id = json.GetProperty("id").GetString()!;
                string title = json.GetProperty("title").GetString()!;
                return (Id: id, Title: title, Index: i);
            });

            var results = await Task.WhenAll(tasks);

            // Assert uniqueness
            results.Length.Should().Be(concurrency);
            var distinctIds = results.Select(r => r.Id).Distinct().ToList();
            distinctIds.Count.Should().Be(concurrency, "All concurrently generated session IDs must be unique");

            // List sessions and verify all 30 exist
            var listResp = await client.GetAsync("/api/sessions");
            listResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
            var listedIds = list.EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToHashSet();

            foreach (var r in results)
            {
                listedIds.Should().Contain(r.Id);
            }

            // Verify detail and initial transcript for sample
            var sample = results[15];
            var detailResp = await client.GetAsync($"/api/sessions/{sample.Id}");
            detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
            detail.GetProperty("session").GetProperty("title").GetString().Should().Be(sample.Title);
            var transcript = detail.GetProperty("transcript");
            transcript.GetArrayLength().Should().Be(1);
            transcript[0].GetProperty("content").GetString().Should().Contain($"worker {sample.Index}");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionsApi_ConcurrentActivationSwitching_ZeroDeadlockUnderParallelContention()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // Create 5 sessions
            var sessionIds = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                var createReq = new CreateSessionApiRequest(Title: $"Activation Target {i}");
                var createResp = await client.PostAsJsonAsync("/api/sessions", createReq);
                var elem = await createResp.Content.ReadFromJsonAsync<JsonElement>();
                sessionIds.Add(elem.GetProperty("id").GetString()!);
            }

            // Concurrently activate different sessions 25 times
            var activationTasks = Enumerable.Range(0, 25).Select(async i =>
            {
                var targetId = sessionIds[i % sessionIds.Count];
                var resp = await client.PostAsync($"/api/sessions/{targetId}/activate", null);
                resp.StatusCode.Should().Be(HttpStatusCode.OK);
                var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
                result.GetProperty("success").GetBoolean().Should().BeTrue();
                result.GetProperty("activeSessionId").GetString().Should().Be(targetId);
            });

            await Task.WhenAll(activationTasks);

            // Verify host is still responsive and healthy
            var listResp = await client.GetAsync("/api/sessions");
            listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionsApi_ConcurrentTranscriptAppendsAndReads_MaintainsChronologicalIntegrity()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            var createReq = new CreateSessionApiRequest(
                Title: "Transcript Concurrency Target",
                Instructions: "Initial system instruction");
            var createResp = await client.PostAsJsonAsync("/api/sessions", createReq);
            var sessionObj = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            string sessionId = sessionObj.GetProperty("id").GetString()!;

            // 10 concurrent reads and 10 concurrent message appends
            var appendTasks = Enumerable.Range(0, 10).Select(async i =>
            {
                var patchReq = new UpdateSessionApiRequest(Instructions: $"Appended instruction message #{i}");
                var patchResp = await client.PatchAsJsonAsync($"/api/sessions/{sessionId}", patchReq);
                patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
            });

            var readTasks = Enumerable.Range(0, 10).Select(async _ =>
            {
                var getResp = await client.GetAsync($"/api/sessions/{sessionId}");
                getResp.StatusCode.Should().Be(HttpStatusCode.OK);
            });

            await Task.WhenAll(appendTasks.Concat(readTasks));

            // Verify final transcript consistency
            var finalResp = await client.GetAsync($"/api/sessions/{sessionId}");
            finalResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var detail = await finalResp.Content.ReadFromJsonAsync<JsonElement>();
            var transcript = detail.GetProperty("transcript");
            // 1 initial + 10 appended = 11 messages
            transcript.GetArrayLength().Should().Be(11);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionsApi_ConcurrentDeletionAndRetrieval_CascadeCleansUpCleanly()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            var createdIds = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                var resp = await client.PostAsJsonAsync("/api/sessions", new CreateSessionApiRequest(Title: $"To Delete #{i}"));
                var elem = await resp.Content.ReadFromJsonAsync<JsonElement>();
                createdIds.Add(elem.GetProperty("id").GetString()!);
            }

            var toDelete = createdIds.Take(5).ToList();
            var toKeep = createdIds.Skip(5).ToList();

            // Delete 5 sessions in parallel
            var deleteTasks = toDelete.Select(async id =>
            {
                var delResp = await client.DeleteAsync($"/api/sessions/{id}");
                delResp.StatusCode.Should().Be(HttpStatusCode.OK);
            });
            await Task.WhenAll(deleteTasks);

            // Verify deleted sessions return 404
            foreach (var id in toDelete)
            {
                var getResp = await client.GetAsync($"/api/sessions/{id}");
                getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
            }

            // Verify kept sessions return 200
            foreach (var id in toKeep)
            {
                var getResp = await client.GetAsync($"/api/sessions/{id}");
                getResp.StatusCode.Should().Be(HttpStatusCode.OK);
            }
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionsApi_PinnedOrdering_PutsPinnedSessionsAtTop()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            var s1Resp = await client.PostAsJsonAsync("/api/sessions", new CreateSessionApiRequest(Title: "Session 1 Unpinned"));
            var s1 = await s1Resp.Content.ReadFromJsonAsync<JsonElement>();
            string s1Id = s1.GetProperty("id").GetString()!;

            var s2Resp = await client.PostAsJsonAsync("/api/sessions", new CreateSessionApiRequest(Title: "Session 2 To Pin"));
            var s2 = await s2Resp.Content.ReadFromJsonAsync<JsonElement>();
            string s2Id = s2.GetProperty("id").GetString()!;

            // Pin session 2
            var patchResp = await client.PatchAsJsonAsync($"/api/sessions/{s2Id}", new UpdateSessionApiRequest(Pinned: true));
            patchResp.StatusCode.Should().Be(HttpStatusCode.OK);

            var listResp = await client.GetAsync("/api/sessions");
            var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
            list.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
            list[0].GetProperty("id").GetString().Should().Be(s2Id, "Pinned session must appear first in listing");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    // =========================================================================
    // SECTION 2: TASKS & AUTONOMOUS EXECUTION STATE MACHINE STRESS TESTS
    // =========================================================================

    [Fact]
    public async Task TasksApi_ConcurrentDispatch_DispatchesMultipleTasksWithUniqueIdentifiers()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            const int count = 10;
            var dispatchTasks = Enumerable.Range(0, count).Select(async i =>
            {
                var req = new DispatchTaskRequest(
                    Prompt: $"Concurrent prompt {i}: perform workspace audit",
                    Model: "gemini-2.5-flash",
                    ToolPermissions: new List<string> { "filesystem" });

                var resp = await client.PostAsJsonAsync("/api/tasks", req);
                resp.StatusCode.Should().Be(HttpStatusCode.OK);
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                return json.GetProperty("id").GetString()!;
            });

            var taskIds = await Task.WhenAll(dispatchTasks);
            taskIds.Length.Should().Be(count);
            taskIds.Distinct().Count().Should().Be(count, "All dispatched task IDs must be globally unique");
            taskIds.Should().AllSatisfy(id => id.Should().StartWith("task_"));

            // Verify GET /api/tasks lists all dispatched tasks
            var listResp = await client.GetAsync("/api/tasks");
            listResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
            var listedIds = list.EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToHashSet();

            foreach (var id in taskIds)
            {
                listedIds.Should().Contain(id);
            }
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TasksApi_RapidConcurrentStateTransitions_PauseResumeCancel_ThreadSafe()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // Dispatch 5 tasks
            var taskIds = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                var req = new DispatchTaskRequest(Prompt: $"Long running task {i} for lifecycle hammering");
                var resp = await client.PostAsJsonAsync("/api/tasks", req);
                var elem = await resp.Content.ReadFromJsonAsync<JsonElement>();
                taskIds.Add(elem.GetProperty("id").GetString()!);
            }

            // Rapidly fire Pause -> Resume -> Cancel on all tasks concurrently
            var transitionTasks = taskIds.Select(async id =>
            {
                var pResp = await client.PostAsync($"/api/tasks/{id}/pause", null);
                pResp.StatusCode.Should().Be(HttpStatusCode.OK);

                await Task.Delay(15);

                var rResp = await client.PostAsync($"/api/tasks/{id}/resume", null);
                rResp.StatusCode.Should().Be(HttpStatusCode.OK);

                await Task.Delay(15);

                var cResp = await client.PostAsync($"/api/tasks/{id}/cancel", null);
                cResp.StatusCode.Should().Be(HttpStatusCode.OK);
            });

            await Task.WhenAll(transitionTasks);

            // Verify final state of all tasks is Cancelled (or Completed if finished before cancel)
            foreach (var id in taskIds)
            {
                var detailResp = await client.GetAsync($"/api/tasks/{id}");
                detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
                var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
                var status = detail.GetProperty("status").GetString();
                status.Should().BeOneOf("Cancelled", "Completed");
            }
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TasksApi_AutonomousTask_RunsToEndToEndCompletionAndRecordsTelemetry()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // Create a session to receive completion
            var sResp = await client.PostAsJsonAsync("/api/sessions", new CreateSessionApiRequest(Title: "Task Telemetry Session"));
            var sElem = await sResp.Content.ReadFromJsonAsync<JsonElement>();
            string sessionId = sElem.GetProperty("id").GetString()!;

            var dispatchReq = new DispatchTaskRequest(
                Prompt: "Generate a summary of project architecture",
                Model: "gemini-2.5-flash",
                SessionId: sessionId,
                ToolPermissions: new List<string> { "filesystem", "terminal" });

            var dispatchResp = await client.PostAsJsonAsync("/api/tasks", dispatchReq);
            dispatchResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var taskObj = await dispatchResp.Content.ReadFromJsonAsync<JsonElement>();
            string taskId = taskObj.GetProperty("id").GetString()!;

            // Poll task until completion (max 10 seconds)
            var sw = Stopwatch.StartNew();
            string finalStatus = "Pending";
            JsonElement finalDetail = default;

            while (sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                var resp = await client.GetAsync($"/api/tasks/{taskId}");
                resp.StatusCode.Should().Be(HttpStatusCode.OK);
                finalDetail = await resp.Content.ReadFromJsonAsync<JsonElement>();
                finalStatus = finalDetail.GetProperty("status").GetString()!;

                if (finalStatus == "Completed" || finalStatus == "Failed" || finalStatus == "Cancelled")
                    break;

                await Task.Delay(50);
            }

            finalStatus.Should().Be("Completed", "Task should run all 4 steps to Completed state");
            finalDetail.GetProperty("progressPercent").GetInt32().Should().Be(100);
            finalDetail.GetProperty("tokensConsumed").GetInt32().Should().BeGreaterThan(0);
            finalDetail.GetProperty("output").GetString().Should().NotBeNullOrWhiteSpace();

            // Verify all 4 steps marked completed
            var steps = finalDetail.GetProperty("steps");
            steps.GetArrayLength().Should().Be(4);
            foreach (var step in steps.EnumerateArray())
            {
                step.GetProperty("status").GetString().Should().Be("Completed");
            }

            // Verify session transcript contains assistant output message
            var sessionResp = await client.GetAsync($"/api/sessions/{sessionId}");
            var sessionDetail = await sessionResp.Content.ReadFromJsonAsync<JsonElement>();
            var transcript = sessionDetail.GetProperty("transcript");
            transcript.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
            var lastMessage = transcript[transcript.GetArrayLength() - 1];
            lastMessage.GetProperty("role").GetString().Should().Be("assistant");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TasksApi_HighFrequencyPollingUnderActiveExecution_ZeroCollectionModificationExceptions()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // Dispatch 3 tasks
            for (int i = 0; i < 3; i++)
            {
                await client.PostAsJsonAsync("/api/tasks", new DispatchTaskRequest(Prompt: $"Polling stress task {i}"));
            }

            // Fire 40 rapid concurrent GET /api/tasks requests while execution is underway
            var pollTasks = Enumerable.Range(0, 40).Select(async _ =>
            {
                var resp = await client.GetAsync("/api/tasks");
                resp.StatusCode.Should().Be(HttpStatusCode.OK);
                var array = await resp.Content.ReadFromJsonAsync<JsonElement>();
                array.GetArrayLength().Should().BeGreaterThanOrEqualTo(3);
            });

            await Task.WhenAll(pollTasks);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TasksApi_NonExistentTaskId_Returns404NotFound()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            string fakeId = "task_nonexistent_999";
            var getResp = await client.GetAsync($"/api/tasks/{fakeId}");
            getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var pauseResp = await client.PostAsync($"/api/tasks/{fakeId}/pause", null);
            pauseResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var resumeResp = await client.PostAsync($"/api/tasks/{fakeId}/resume", null);
            resumeResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var cancelResp = await client.PostAsync($"/api/tasks/{fakeId}/cancel", null);
            cancelResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    // =========================================================================
    // SECTION 3: FILESYSTEM PATH SECURITY & DIFF CHALLENGE TESTS
    // =========================================================================

    [Fact]
    public async Task FilesystemApi_StandardOperations_ReadWriteOverwriteDiff_WorksAsExpected()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // 1. Write file in nested subdirectory
            var writeReq = new FsWriteRequest("nested/dir/test.txt", "Initial file content\nLine 2", CreateDirectory: true, Overwrite: false);
            var writeResp = await client.PostAsJsonAsync("/api/fs/write", writeReq);
            writeResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 2. Read file
            var readResp = await client.GetAsync("/api/fs/read?path=nested/dir/test.txt");
            readResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var readJson = await readResp.Content.ReadFromJsonAsync<JsonElement>();
            readJson.GetProperty("content").GetString().Should().Be("Initial file content\nLine 2");

            // 3. Write without overwrite flag should return 409 Conflict
            var conflictReq = new FsWriteRequest("nested/dir/test.txt", "New content", CreateDirectory: false, Overwrite: false);
            var conflictResp = await client.PostAsJsonAsync("/api/fs/write", conflictReq);
            conflictResp.StatusCode.Should().Be(HttpStatusCode.Conflict);

            // 4. Write without createDirectory for non-existent dir should return 400 BadRequest
            var badDirReq = new FsWriteRequest("nonexistent_folder/file.txt", "Content", CreateDirectory: false, Overwrite: false);
            var badDirResp = await client.PostAsJsonAsync("/api/fs/write", badDirReq);
            badDirResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // 5. Diff computation
            var diffReq = new FsDiffRequest("nested/dir/test.txt", "Initial file content\nModified Line 2\nLine 3");
            var diffResp = await client.PostAsJsonAsync("/api/fs/diff", diffReq);
            diffResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var diffJson = await diffResp.Content.ReadFromJsonAsync<JsonElement>();
            diffJson.GetProperty("hasChanges").GetBoolean().Should().BeTrue();
            diffJson.GetProperty("unifiedDiff").GetString().Should().Contain("+Modified Line 2");

            // 6. Approve diff
            var approveResp = await client.PostAsJsonAsync("/api/fs/diff/approve", new FsDiffApproveRequest(Path: "nested/dir/test.txt"));
            approveResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // Verify content updated
            var reRead = await client.GetAsync("/api/fs/read?path=nested/dir/test.txt");
            var reReadJson = await reRead.Content.ReadFromJsonAsync<JsonElement>();
            reReadJson.GetProperty("content").GetString().Should().Contain("Modified Line 2");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task FilesystemApi_PathTraversalSecurity_EmpiricallyProbesWorkspaceBoundaryEscapes()
    {
        // Setup a canary file strictly OUTSIDE the workspace directory
        var parentDir = Path.Combine(Path.GetTempPath(), "humofa-security-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parentDir);

        var workspaceDir = Path.Combine(parentDir, "workspace");
        Directory.CreateDirectory(workspaceDir);

        var outsideCanaryFile = Path.Combine(parentDir, "outside_canary.txt");
        const string secretContent = "TOP_SECRET_OUTSIDE_WORKSPACE_TOKEN_987654";
        await File.WriteAllTextAsync(outsideCanaryFile, secretContent);

        var (host, client, _) = await CreateTestHostAsync(explicitWorkDir: workspaceDir);
        try
        {
            // --- ATTACK VECTOR 1: Relative Path Traversal Read with '../' ---
            var relativeSlashResp = await client.GetAsync("/api/fs/read?path=../outside_canary.txt");
            relativeSlashResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Path traversal with ../ must be blocked with 400 BadRequest");
            var relativeSlashBody = await relativeSlashResp.Content.ReadAsStringAsync();
            relativeSlashBody.Should().Contain("Access denied: Path outside workspace boundary.");

            // --- ATTACK VECTOR 2: Windows Backslash Traversal Read with '..\' ---
            var relativeBackslashResp = await client.GetAsync($"/api/fs/read?path={Uri.EscapeDataString("..\\outside_canary.txt")}");
            relativeBackslashResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Path traversal with ..\\ must be blocked with 400 BadRequest");
            var relativeBackslashBody = await relativeBackslashResp.Content.ReadAsStringAsync();
            relativeBackslashBody.Should().Contain("Access denied: Path outside workspace boundary.");

            // --- ATTACK VECTOR 3: Absolute Path Read Outside Workspace ---
            var absReadResp = await client.GetAsync($"/api/fs/read?path={Uri.EscapeDataString(outsideCanaryFile)}");
            absReadResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Absolute path outside workspace boundary must be blocked with 400 BadRequest");
            var absReadBody = await absReadResp.Content.ReadAsStringAsync();
            absReadBody.Should().Contain("Access denied: Path outside workspace boundary.");

            // --- ATTACK VECTOR 4: Relative Path Traversal Write with '../' ---
            var writeTraversalPath = "../injected_outside_file.txt";
            var writeReq = new FsWriteRequest(writeTraversalPath, "INJECTED_PAYLOAD_OUTSIDE", CreateDirectory: false, Overwrite: true);
            var writeResp = await client.PostAsJsonAsync("/api/fs/write", writeReq);
            writeResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Write path traversal must be blocked with 400 BadRequest");

            string expectedInjectedFile = Path.Combine(parentDir, "injected_outside_file.txt");
            bool fileWasCreatedOutside = File.Exists(expectedInjectedFile);
            fileWasCreatedOutside.Should().BeFalse("Injected file must not be created outside workspace boundary");

            // Clean up injected file if created
            if (fileWasCreatedOutside)
            {
                File.Delete(expectedInjectedFile);
            }

            // --- ATTACK VECTOR 5: Empty Path Input Check ---
            var emptyPathResp = await client.GetAsync("/api/fs/read?path=");
            emptyPathResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Empty path must return 400 BadRequest");

            var emptyWriteResp = await client.PostAsJsonAsync("/api/fs/write", new FsWriteRequest("", "content"));
            emptyWriteResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Empty path write must return 400 BadRequest");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
            try
            {
                if (Directory.Exists(parentDir))
                    Directory.Delete(parentDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    // =========================================================================
    // SECTION 4: SETTINGS PERSISTENCE & RUNTIME RE-INITIALIZATION TESTS
    // =========================================================================

    [Fact]
    public async Task SettingsApi_PersistenceAndRuntimeUpdate_SurvivesHostReinitialization()
    {
        var sharedWorkDir = Path.Combine(Path.GetTempPath(), "humofa-settings-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedWorkDir);

        try
        {
            // === PHASE 1: Start Host 1 and save new configuration ===
            var host1 = new DesktopHost(workspaceDir: sharedWorkDir);
            var url1 = await host1.StartAsync(port: 0);
            using var client1 = new HttpClient { BaseAddress = new Uri(url1) };

            // 1. Initial GET check
            var initialGetResp = await client1.GetAsync("/api/settings");
            initialGetResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 2. Update settings with unmasked API keys, new theme, and default model
            var newSettings = new DesktopSettings
            {
                Appearance = new AppearanceSettings
                {
                    Theme = "light",
                    FontSize = 16,
                    FontFamily = "Cascadia Code",
                    CompactSidebar = true
                },
                Models = new ModelSettings
                {
                    DefaultModel = "gemini-2.5-pro-challenger",
                    FallbackModel = "claude-3-7-sonnet"
                },
                ApiKeys = new ApiKeySettings
                {
                    GeminiApiKey = "AIzaSyChallengeKey-Gemini-987654321",
                    OpenAiApiKey = "sk-proj-ChallengeKey-OpenAI-123456789",
                    AnthropicApiKey = "sk-ant-ChallengeKey-Anthropic-456789012",
                    OpenRouterApiKey = "sk-or-ChallengeKey-OpenRouter-345678901"
                },
                TelemetryEnabled = false
            };

            var postResp = await client1.PostAsJsonAsync("/api/settings", newSettings);
            postResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var postJson = await postResp.Content.ReadFromJsonAsync<DesktopSettings>();

            // Verify POST response returns masked keys
            postJson!.ApiKeys.GeminiApiKey.Should().Contain("***");
            postJson.Appearance.Theme.Should().Be("light");
            postJson.Models.DefaultModel.Should().Be("gemini-2.5-pro-challenger");

            // Verify runtime environment variable updated immediately
            Environment.GetEnvironmentVariable("GEMINI_API_KEY").Should().Be("AIzaSyChallengeKey-Gemini-987654321");
            Environment.GetEnvironmentVariable("OPENAI_API_KEY").Should().Be("sk-proj-ChallengeKey-OpenAI-123456789");

            // Shut down Host 1 completely
            client1.Dispose();
            await host1.DisposeAsync();

            // Verify settings.json file exists on disk
            var settingsFile = Path.Combine(sharedWorkDir, "settings.json");
            File.Exists(settingsFile).Should().BeTrue("settings.json must be persisted to the workspace directory");

            // Read disk contents to confirm unmasked credentials were saved
            var diskJson = await File.ReadAllTextAsync(settingsFile);
            diskJson.Should().Contain("AIzaSyChallengeKey-Gemini-987654321");
            diskJson.Should().Contain("gemini-2.5-pro-challenger");

            // === PHASE 2: Start a brand new Host 2 instance pointing to the same workspace directory ===
            var host2 = new DesktopHost(workspaceDir: sharedWorkDir);
            var url2 = await host2.StartAsync(port: 0);
            using var client2 = new HttpClient { BaseAddress = new Uri(url2) };

            var host2GetResp = await client2.GetAsync("/api/settings");
            host2GetResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var host2Settings = await host2GetResp.Content.ReadFromJsonAsync<DesktopSettings>();

            // Verify all configurations persisted and reloaded cleanly
            host2Settings.Should().NotBeNull();
            host2Settings!.Appearance.Theme.Should().Be("light");
            host2Settings.Appearance.FontSize.Should().Be(16);
            host2Settings.Appearance.FontFamily.Should().Be("Cascadia Code");
            host2Settings.Appearance.CompactSidebar.Should().BeTrue();
            host2Settings.Models.DefaultModel.Should().Be("gemini-2.5-pro-challenger");
            host2Settings.Models.FallbackModel.Should().Be("claude-3-7-sonnet");
            host2Settings.TelemetryEnabled.Should().BeFalse();

            // Verify GET response on host 2 keeps API keys masked
            host2Settings.ApiKeys.GeminiApiKey.Should().Contain("***");
            host2Settings.ApiKeys.OpenAiApiKey.Should().Contain("***");

            client2.Dispose();
            await host2.DisposeAsync();
        }
        finally
        {
            try
            {
                if (Directory.Exists(sharedWorkDir))
                    Directory.Delete(sharedWorkDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task SettingsApi_ValidationRules_RejectsInvalidPayloads()
    {
        var (host, client, workDir) = await CreateTestHostAsync();
        try
        {
            // 1. Invalid theme should return 400
            var invalidThemeReq = new DesktopSettings
            {
                Appearance = new AppearanceSettings { Theme = "invalid-theme-neon" }
            };
            var themeResp = await client.PostAsJsonAsync("/api/settings", invalidThemeReq);
            themeResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var themeErr = await themeResp.Content.ReadFromJsonAsync<JsonElement>();
            themeErr.GetProperty("error").GetString().Should().Contain("Theme must be 'dark', 'light', or 'system'");

            // 2. Empty DefaultModel should return 400
            var emptyModelReq = new DesktopSettings
            {
                Models = new ModelSettings { DefaultModel = "   " }
            };
            var modelResp = await client.PostAsJsonAsync("/api/settings", emptyModelReq);
            modelResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var modelErr = await modelResp.Content.ReadFromJsonAsync<JsonElement>();
            modelErr.GetProperty("error").GetString().Should().Contain("DefaultModel cannot be empty");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    // =========================================================================
    // SECTION 5: REMEDIATION VERIFICATION TESTS
    // =========================================================================

    [Fact]
    public async Task TasksApi_StateTransitionGuards_RejectsInvalidTransitionsWithBadRequest()
    {
        var (host, client, _) = await CreateTestHostAsync();
        try
        {
            var dispatchResp = await client.PostAsJsonAsync("/api/tasks", new DispatchTaskRequest(Prompt: "Task for guard tests"));
            dispatchResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var taskElem = await dispatchResp.Content.ReadFromJsonAsync<JsonElement>();
            var taskId = taskElem.GetProperty("id").GetString()!;

            // 1. Task is Running -> Pause should succeed
            var pauseResp = await client.PostAsync($"/api/tasks/{taskId}/pause", null);
            pauseResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 2. Task is Paused -> Pausing AGAIN must return 400 BadRequest
            var pauseAgainResp = await client.PostAsync($"/api/tasks/{taskId}/pause", null);
            pauseAgainResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var pauseAgainErr = await pauseAgainResp.Content.ReadAsStringAsync();
            pauseAgainErr.Should().Contain("Cannot pause task");

            // 3. Task is Paused -> Resume should succeed
            var resumeResp = await client.PostAsync($"/api/tasks/{taskId}/resume", null);
            resumeResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 4. Task is Running -> Resuming AGAIN must return 400 BadRequest
            var resumeAgainResp = await client.PostAsync($"/api/tasks/{taskId}/resume", null);
            resumeAgainResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var resumeAgainErr = await resumeAgainResp.Content.ReadAsStringAsync();
            resumeAgainErr.Should().Contain("Cannot resume task");

            // 5. Cancel task -> Should succeed
            var cancelResp = await client.PostAsync($"/api/tasks/{taskId}/cancel", null);
            cancelResp.StatusCode.Should().Be(HttpStatusCode.OK);

            // 6. Task is Cancelled -> Pause, Resume, Cancel must ALL return 400 BadRequest
            var invalidPause = await client.PostAsync($"/api/tasks/{taskId}/pause", null);
            invalidPause.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var invalidResume = await client.PostAsync($"/api/tasks/{taskId}/resume", null);
            invalidResume.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var invalidCancel = await client.PostAsync($"/api/tasks/{taskId}/cancel", null);
            invalidCancel.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task FilesystemApi_DiffAndTreePathTraversal_RejectsEscapesWithBadRequest()
    {
        var (host, client, _) = await CreateTestHostAsync();
        try
        {
            // 1. GET /api/fs/tree with traversal
            var treeResp = await client.GetAsync("/api/fs/tree?path=../../escape_tree");
            treeResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var treeErr = await treeResp.Content.ReadAsStringAsync();
            treeErr.Should().Contain("Access denied: Path outside workspace boundary.");

            // 2. GET /api/fs/diff with traversal
            var diffGetResp = await client.GetAsync("/api/fs/diff?path=../../escape_diff.txt");
            diffGetResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var diffGetErr = await diffGetResp.Content.ReadAsStringAsync();
            diffGetErr.Should().Contain("Access denied: Path outside workspace boundary.");

            // 3. POST /api/fs/diff with traversal
            var diffPostResp = await client.PostAsJsonAsync("/api/fs/diff", new FsDiffRequest("../outside.txt", "new content"));
            diffPostResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var diffPostErr = await diffPostResp.Content.ReadAsStringAsync();
            diffPostErr.Should().Contain("Access denied: Path outside workspace boundary.");
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void SettingsApi_MaskKey_ProtectsShortKeysCompletely()
    {
        // Null / whitespace
        DesktopSettings.MaskKey(null).Should().Be(string.Empty);
        DesktopSettings.MaskKey("   ").Should().Be(string.Empty);

        // <= 8 characters must be masked completely with "***"
        DesktopSettings.MaskKey("1").Should().Be("***");
        DesktopSettings.MaskKey("12345").Should().Be("***");
        DesktopSettings.MaskKey("123456").Should().Be("***");
        DesktopSettings.MaskKey("1234567").Should().Be("***");
        DesktopSettings.MaskKey("12345678").Should().Be("***");

        // > 8 characters exposes first 3 and last 4
        var key9 = DesktopSettings.MaskKey("123456789");
        key9.Should().Be("123***6789");

        var key25 = DesktopSettings.MaskKey("AIzaSyD-TestKey1234567890");
        key25.Should().StartWith("AIz***");
        key25.Should().EndWith("7890");
    }

    [Fact]
    public async Task WebSocket_ConcurrentMultiThreadedBroadcast_AllSendsSucceedWithoutException()
    {
        var (host, client, _) = await CreateTestHostAsync();
        var wsClient = new ClientWebSocket();
        try
        {
            var wsUri = new Uri(host.BaseUrl.Replace("http://", "ws://") + "/ws/live");
            await wsClient.ConnectAsync(wsUri, CancellationToken.None);
            wsClient.State.Should().Be(WebSocketState.Open);

            // Fire 30 concurrent broadcast calls across multiple threads
            var broadcastTasks = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
            {
                await host.BroadcastStreamChunkAsync("task_concurrent", "session_1", $"chunk {i}", i == 29);
                await host.BroadcastTerminalOutputAsync("dir", $"output line {i}", false);
            }));

            var aggregateTask = Task.WhenAll(broadcastTasks);
            var timeoutTask = Task.Delay(5000);

            var completed = await Task.WhenAny(aggregateTask, timeoutTask);
            completed.Should().Be(aggregateTask, "All concurrent broadcasts must complete without deadlock or collision");

            // Client should still be healthy and connected
            wsClient.State.Should().Be(WebSocketState.Open);
        }
        finally
        {
            try
            {
                if (wsClient.State == WebSocketState.Open)
                {
                    await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Close test", CancellationToken.None);
                }
            }
            catch { }
            wsClient.Dispose();
            client.Dispose();
            await host.DisposeAsync();
        }
    }
}
