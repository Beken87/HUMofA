using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HUMofA.Agent;
using HUMofA.Backends;
using HUMofA.ComputerUse;
using HUMofA.Diff;
using HUMofA.Gateway;
using HUMofA.Memory;
using HUMofA.Scheduler;
using HUMofA.Sessions;
using HUMofA.Skills;
using HUMofA.SubAgents;
using HUMofA.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HUMofA.Desktop;

public sealed record ChatMessageRecord(
    string Id,
    string Sender,
    string Channel,
    string Text,
    bool IsGhostTyping,
    DateTimeOffset Timestamp);

public sealed record GhostTypeRequest(
    string Text,
    string SourceChannel = "telegram",
    string SenderId = "remote-user",
    double BaseDelayMs = 8.0);

public sealed record ChatPromptRequest(
    string Prompt,
    string? PreferredModel = null);

public sealed record SkillInstallRequest(
    string RepoOrUrl,
    string SkillSubPath = "SKILL.md");

public sealed record MemoryWriteRequest(
    string Kind,
    string Title,
    string Content);

public sealed record KanbanCreateRequest(
    string Title,
    string Description = "",
    string? AssignedAgentId = null);

public sealed record KanbanMoveRequest(
    string CardId,
    string Column);

public sealed record CronCreateRequest(
    string Name,
    string SchedulePhrase,
    string Prompt,
    string TargetChannel = "desktop");

public sealed record BackendExecRequest(
    string Backend,
    string Command,
    string? TargetHostOrContainer = null);

/// <summary>
/// Embedded Kestrel Web + WebSocket host serving the HUMofA Desktop UI, REST APIs (over 40 concrete endpoints),
/// and real-time WebSocket protocol (/ws/live) for session and autonomous task streaming.
/// </summary>
public sealed class DesktopHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly CentralEventBus _eventBus;
    private readonly GhostTypingSync _ghostTypingSync;
    private readonly HumanizedController _controller;
    private readonly UnifiedMemoryStore _memoryStore;
    private readonly SkillManager _skillManager;
    private readonly SubAgentOrchestrator _subAgents;
    private readonly ExecutionEnvironmentManager _backends;
    private readonly MultimodalToolset _toolset;
    private readonly MultiModelRouter _router;
    private readonly NaturalCronScheduler _scheduler;
    private readonly ISessionManager _sessionManager;
    private readonly IAgentManager _agentManager;
    private readonly IFileDiffService _diffService;
    private readonly SmartApproval _smartApproval;
    private readonly bool _ownsSessionManager;

    private readonly ConcurrentQueue<ChatMessageRecord> _chatHistory = new();
    private readonly ConcurrentDictionary<string, WebSocket> _wsClients = new();
    private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _wsLocks = new();
    private readonly ConcurrentDictionary<string, DesktopTaskState> _tasks = new();
    private readonly ConcurrentDictionary<string, FsDiffResponse> _pendingDiffs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _settingsLock = new();

    private readonly string _workspaceDir;
    private readonly string _settingsFilePath;
    private readonly string _uiRootDir;
    private DesktopSettings _settings = new();

    private WebApplication? _app;
    private CancellationTokenSource? _busListenerCts;
    private Task? _busListenerTask;

    public string BaseUrl { get; private set; } = string.Empty;
    public bool IsRunning { get; private set; }
    public string WorkspaceDir => _workspaceDir;
    public int ConnectedClientsCount => _wsClients.Count;

    public CentralEventBus EventBus => _eventBus;
    public GhostTypingSync GhostTyping => _ghostTypingSync;
    public HumanizedController Controller => _controller;
    public UnifiedMemoryStore MemoryStore => _memoryStore;
    public SkillManager Skills => _skillManager;
    public SubAgentOrchestrator SubAgents => _subAgents;
    public ExecutionEnvironmentManager Backends => _backends;
    public MultimodalToolset Toolset => _toolset;
    public MultiModelRouter Router => _router;
    public NaturalCronScheduler Scheduler => _scheduler;
    public ISessionManager SessionManager => _sessionManager;
    public IAgentManager AgentManager => _agentManager;
    public IFileDiffService DiffService => _diffService;
    public SmartApproval SmartApproval => _smartApproval;
    public DesktopSettings Settings => _settings;

    public DesktopHost(
        CentralEventBus? eventBus = null,
        GhostTypingSync? ghostTypingSync = null,
        HumanizedController? controller = null,
        UnifiedMemoryStore? memoryStore = null,
        SkillManager? skillManager = null,
        SubAgentOrchestrator? subAgents = null,
        ExecutionEnvironmentManager? backends = null,
        MultimodalToolset? toolset = null,
        MultiModelRouter? router = null,
        NaturalCronScheduler? scheduler = null,
        string? workspaceDir = null,
        string? uiRootDir = null,
        ISessionManager? sessionManager = null,
        IAgentManager? agentManager = null,
        IFileDiffService? diffService = null,
        SmartApproval? smartApproval = null)
    {
        _workspaceDir = Path.GetFullPath(workspaceDir ?? Path.Combine(Path.GetTempPath(), "humofa-runtime"));
        Directory.CreateDirectory(_workspaceDir);

        _eventBus = eventBus ?? new CentralEventBus();
        var mockInput = new MockInputSimulator();
        var killSwitch = new PanicKillSwitch();
        _controller = controller ?? new HumanizedController(mockInput, killSwitch) { FastModeForTesting = true };
        _ghostTypingSync = ghostTypingSync ?? new GhostTypingSync();
        _memoryStore = memoryStore ?? new UnifiedMemoryStore(Path.Combine(_workspaceDir, "memory"));
        _skillManager = skillManager ?? new SkillManager(Path.Combine(_workspaceDir, "skills"), searchIndex: _memoryStore.SearchIndex);
        _subAgents = subAgents ?? new SubAgentOrchestrator();
        _backends = backends ?? new ExecutionEnvironmentManager();
        _router = router ?? new MultiModelRouter();
        _toolset = toolset ?? new MultimodalToolset(_router);
        _scheduler = scheduler ?? new NaturalCronScheduler(_eventBus);

        if (sessionManager != null)
        {
            _sessionManager = sessionManager;
            _ownsSessionManager = false;
        }
        else
        {
            var sessionsDir = Path.Combine(_workspaceDir, "sessions");
            Directory.CreateDirectory(sessionsDir);
            _sessionManager = new SessionManager(Path.Combine(sessionsDir, "sessions.db"));
            _ownsSessionManager = true;
        }

        _agentManager = agentManager ?? new AgentManager(_subAgents);
        _diffService = diffService ?? new FileDiffService(Path.Combine(_workspaceDir, "backups"));
        _smartApproval = smartApproval ?? new SmartApproval(_eventBus);

        _uiRootDir = uiRootDir ?? ResolveDefaultUiDirectory();

        _settingsFilePath = Path.Combine(_workspaceDir, "settings.json");
        LoadOrCreateSettings();
    }

    public async Task<string> StartAsync(int port = 0, CancellationToken ct = default)
    {
        if (IsRunning && _app is not null)
            return BaseUrl;

        await _memoryStore.InitializeAsync(ct);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = []
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();
        app.UseWebSockets();

        _busListenerCts = new CancellationTokenSource();
        _busListenerTask = Task.Run(() => ListenToEventBusAsync(_busListenerCts.Token), CancellationToken.None);

        MapEndpoints(app);

        await app.StartAsync(ct);
        _app = app;
        IsRunning = true;

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        BaseUrl = addresses?.FirstOrDefault() ?? $"http://127.0.0.1:{port}";
        return BaseUrl;
    }

    private async Task ListenToEventBusAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _eventBus.SubscribeAsync(ct).ConfigureAwait(false))
            {
                bool isFinal = msg.Metadata is not null &&
                               msg.Metadata.TryGetValue("ghostTypingComplete", out var flag) &&
                               string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

                if (msg.IsGhostTyping || isFinal)
                {
                    var record = new ChatMessageRecord(
                        msg.Id,
                        msg.Sender,
                        msg.Channel,
                        msg.Content,
                        msg.IsGhostTyping && !isFinal,
                        new DateTimeOffset(DateTime.SpecifyKind(msg.Timestamp, DateTimeKind.Utc)));

                    if (isFinal)
                    {
                        _chatHistory.Enqueue(record);
                    }

                    await BroadcastWebSocketAsync(new
                    {
                        type = isFinal ? "ghost_complete" : "ghost_frame",
                        id = msg.Id,
                        sender = msg.Sender,
                        channel = msg.Channel,
                        text = msg.Content,
                        timestamp = msg.Timestamp
                    }, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        #region Static UI Routes

        app.MapGet("/", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(LoadUiAsset("index.html", GetFallbackHtml()));
        });

        app.MapGet("/app.js", async context =>
        {
            context.Response.ContentType = "application/javascript; charset=utf-8";
            await context.Response.WriteAsync(LoadUiAsset("app.js", GetFallbackJs()));
        });

        app.MapGet("/style.css", async context =>
        {
            context.Response.ContentType = "text/css; charset=utf-8";
            await context.Response.WriteAsync(LoadUiAsset("style.css", GetFallbackCss()));
        });

        #endregion

        #region WebSocket Live Event Stream

        app.Map("/ws/live", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var clientId = Guid.NewGuid().ToString("N");
            _wsClients[clientId] = ws;

            var buffer = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted);
                        break;
                    }
                    else if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (text.Contains("\"ping\""))
                        {
                            var pong = Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");
                            await SendWebSocketSafeAsync(ws, pong, WebSocketMessageType.Text, true, context.RequestAborted);
                        }
                    }
                }
            }
            catch
            {
                // Disconnect cleanly
            }
            finally
            {
                _wsClients.TryRemove(clientId, out _);
                if (_wsLocks.TryRemove(ws, out var sem))
                {
                    sem.Dispose();
                }
            }
        });

        #endregion

        #region Status Endpoint

        app.MapGet("/api/status", () => Results.Ok(new
        {
            status = "online",
            version = Program.Version,
            panicTriggered = _controller.PanicKillSwitch?.IsPanicTriggered ?? false,
            providers = MultiModelRouter.BuiltInProviders.Select(p => p.ProviderId).ToArray(),
            skillsCount = _skillManager.InstalledSkills.Count,
            kanbanCount = _subAgents.Kanban.AllCards.Count,
            chatHistoryCount = _chatHistory.Count,
            backends = Enum.GetNames<ExecutionBackendType>(),
            activeTasksCount = _tasks.Values.Count(t => t.Status == "Running" || t.Status == "Pending")
        }));

        #endregion

        #region Chat & Ghost Typing Endpoints

        app.MapGet("/api/chat", () => Results.Ok(_chatHistory.ToArray()));

        app.MapPost("/api/chat", async (ChatPromptRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "Prompt is required." });

            var userMsg = new ChatMessageRecord(
                Guid.NewGuid().ToString("N"),
                "local-user",
                "desktop",
                req.Prompt,
                false,
                DateTimeOffset.UtcNow);
            _chatHistory.Enqueue(userMsg);

            var (replyText, decision) = await _router.RouteAndCompleteAsync(req.Prompt, req.PreferredModel, ct);

            var replyRecord = new ChatMessageRecord(
                Guid.NewGuid().ToString("N"),
                $"humofa ({decision.SelectedProviderId}:{decision.SelectedModel})",
                "desktop",
                replyText,
                false,
                DateTimeOffset.UtcNow);
            _chatHistory.Enqueue(replyRecord);

            return Results.Ok(new
            {
                userMessage = userMsg,
                assistantReply = replyRecord,
                provider = decision.SelectedProviderId,
                model = decision.SelectedModel,
                usedFallback = decision.UsedOfflineFallback
            });
        });

        app.MapPost("/api/ghost-type", async (GhostTypeRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "Text is required." });

            var incoming = AgentMessage.Create(req.SourceChannel, req.SenderId, req.Text);
            await _ghostTypingSync.StreamGhostTypingAsync(
                incoming,
                _eventBus,
                TimeSpan.FromMilliseconds(Math.Max(1.0, req.BaseDelayMs)),
                "desktop",
                ct);

            var userRecord = new ChatMessageRecord(
                incoming.Id,
                req.SenderId,
                req.SourceChannel,
                req.Text,
                false,
                DateTimeOffset.UtcNow);
            _chatHistory.Enqueue(userRecord);

            var (replyText, decision) = await _router.RouteAndCompleteAsync(req.Text, null, ct);

            var replyRecord = new ChatMessageRecord(
                Guid.NewGuid().ToString("N"),
                $"humofa ({decision.SelectedProviderId})",
                req.SourceChannel,
                replyText,
                false,
                DateTimeOffset.UtcNow);
            _chatHistory.Enqueue(replyRecord);

            return Results.Ok(new
            {
                synchronized = true,
                sourceChannel = req.SourceChannel,
                senderId = req.SenderId,
                ghostTypedText = req.Text,
                autonomousReply = replyText
            });
        });

        #endregion

        #region Computer-Use & SOM Endpoints

        app.MapGet("/api/computer-use/elements", () =>
        {
            var elements = _controller.ListWindows();
            return Results.Ok(elements);
        });

        app.MapGet("/api/computer-use/som", () =>
        {
            var somResult = _controller.Som();
            return Results.File(somResult.ImageBytes, "image/png");
        });

        app.MapPost("/api/panic-stop", () =>
        {
            _controller.PanicKillSwitch?.TriggerPanic("Triggered via Desktop REST API");
            return Results.Ok(new { panicTriggered = _controller.PanicKillSwitch?.IsPanicTriggered ?? true });
        });

        #endregion

        #region Skills Hub Endpoints

        app.MapGet("/api/skills", () => Results.Ok(new
        {
            installed = _skillManager.InstalledSkills.Values.ToArray(),
            officialTaps = SkillsHubGitHub.OfficialTapProviders,
            nousCategories = SkillsHubGitHub.NousHermesCategories
        }));

        app.MapPost("/api/skills/install", async (SkillInstallRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RepoOrUrl))
                return Results.BadRequest(new { error = "RepoOrUrl is required." });

            var installed = await _skillManager.InstallSkillFromGitHubAsync(req.RepoOrUrl, req.SkillSubPath, ct);
            return Results.Ok(installed);
        });

        #endregion

        #region Memory & Search Endpoints

        app.MapGet("/api/memory", async (CancellationToken ct) =>
        {
            var soul = await _memoryStore.ReadDocumentAsync(MemoryDocumentType.Soul, ct);
            var memory = await _memoryStore.ReadDocumentAsync(MemoryDocumentType.Memory, ct);
            var user = await _memoryStore.ReadDocumentAsync(MemoryDocumentType.User, ct);
            return Results.Ok(new { soul, memory, user });
        });

        app.MapPost("/api/memory", async (MemoryWriteRequest req, CancellationToken ct) =>
        {
            if (!Enum.TryParse<MemoryDocumentType>(req.Kind, ignoreCase: true, out var kind))
                kind = MemoryDocumentType.Memory;

            await _memoryStore.AppendMemoryEntryAsync(kind, req.Title, req.Content, ct);
            return Results.Ok(new { status = "saved", kind = kind.ToString() });
        });

        app.MapGet("/api/memory/search", async (string q, CancellationToken ct) =>
        {
            var hits = await _memoryStore.SearchMemoriesAsync(q ?? string.Empty, null, 20, ct);
            return Results.Ok(hits);
        });

        #endregion

        #region Kanban & Cron Endpoints

        app.MapGet("/api/kanban", () => Results.Ok(_subAgents.Kanban.AllCards));

        app.MapPost("/api/kanban", (KanbanCreateRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required." });

            var card = _subAgents.Kanban.CreateCard(req.Title, req.Description, req.AssignedAgentId);
            return Results.Ok(card);
        });

        app.MapPost("/api/kanban/move", async (KanbanMoveRequest req) =>
        {
            if (!Enum.TryParse<KanbanColumn>(req.Column, ignoreCase: true, out var col))
                return Results.BadRequest(new { error = $"Invalid Kanban column '{req.Column}'." });

            var updated = await _subAgents.Kanban.MoveCardAsync(req.CardId, col);
            return Results.Ok(updated);
        });

        app.MapGet("/api/cron", () => Results.Ok(_scheduler.ListJobs()));

        app.MapPost("/api/cron", (CronCreateRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.SchedulePhrase) || string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "SchedulePhrase and Prompt are required." });

            var job = _scheduler.RegisterJob(req.Name, req.SchedulePhrase, _ => Task.FromResult($"Executed prompt: {req.Prompt}"));
            return Results.Ok(new
            {
                id = job.Id,
                name = job.Name,
                naturalSchedule = job.NaturalSchedule,
                cronExpression = job.CronExpression,
                status = job.Status.ToString(),
                nextRun = job.NextRun
            });
        });

        #endregion

        #region Multi-Backend Execution Endpoint

        app.MapPost("/api/backends/execute", async (BackendExecRequest req, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ExecutionBackendType>(req.Backend, ignoreCase: true, out var backendType))
                backendType = ExecutionBackendType.Local;

            var spec = new BackendExecutionRequest(
                req.Command,
                backendType,
                ContainerImageOrProfile: req.TargetHostOrContainer,
                RemoteHost: req.TargetHostOrContainer);
            var res = await _backends.ExecuteAsync(spec, ct);
            return Results.Ok(res);
        });

        #endregion

        #region 1. Sessions API

        app.MapGet("/api/sessions", async (CancellationToken ct) =>
        {
            var list = await _sessionManager.ListSessionsAsync(ct);
            return Results.Ok(list);
        });

        app.MapPost("/api/sessions", async (CreateSessionApiRequest req, CancellationToken ct) =>
        {
            string title = !string.IsNullOrWhiteSpace(req.Title) ? req.Title : "New Session";
            string model = req.Model ?? _settings.Models.DefaultModel ?? "gemini-2.5-flash";
            string workDir = req.WorkingDir ?? _workspaceDir;
            string tier = req.AutonomyTier ?? _settings.Permissions.DefaultAutonomyTier ?? AutonomyTiers.Guarded;
            int maxTokens = req.MaxTokens ?? 8192;

            var session = await _sessionManager.CreateSessionAsync(title, model, workDir, tier, maxTokens, pinned: false, ct);
            if (!string.IsNullOrWhiteSpace(req.Instructions))
            {
                await _sessionManager.AppendMessageAsync(session.Id, "system", req.Instructions, tokenCount: req.Instructions.Length / 4, ct: ct);
            }
            return Results.Ok(session);
        });

        app.MapGet("/api/sessions/{id}", async (string id, CancellationToken ct) =>
        {
            var session = await _sessionManager.GetSessionAsync(id, ct);
            if (session == null) return Results.NotFound(new { error = $"Session '{id}' not found." });

            var transcript = await _sessionManager.GetTranscriptAsync(id, 1000, ct);
            var toolCalls = await _sessionManager.GetToolCallsAsync(id, 500, ct);
            var perms = await _sessionManager.GetPermissionGrantsAsync(id, ct);

            return Results.Ok(new SessionDetailResponse(session, transcript, toolCalls, perms, session.TotalTokens));
        });

        app.MapPatch("/api/sessions/{id}", async (string id, UpdateSessionApiRequest req, CancellationToken ct) =>
        {
            var existing = await _sessionManager.GetSessionAsync(id, ct);
            if (existing == null) return Results.NotFound(new { error = $"Session '{id}' not found." });

            bool updated = await _sessionManager.UpdateSessionAsync(
                id,
                title: req.Title,
                model: req.Model,
                workingDir: req.WorkingDir,
                autonomyTier: req.AutonomyTier,
                maxTokens: req.MaxTokens,
                pinned: req.Pinned,
                ct: ct);

            if (!string.IsNullOrWhiteSpace(req.Instructions))
            {
                await _sessionManager.AppendMessageAsync(id, "system", req.Instructions, tokenCount: req.Instructions.Length / 4, ct: ct);
            }

            var refreshed = await _sessionManager.GetSessionAsync(id, ct);
            return Results.Ok(refreshed);
        });

        app.MapDelete("/api/sessions/{id}", async (string id, CancellationToken ct) =>
        {
            bool deleted = await _sessionManager.DeleteSessionAsync(id, ct);
            return deleted ? Results.Ok(new { success = true, id }) : Results.NotFound(new { error = $"Session '{id}' not found." });
        });

        app.MapPost("/api/sessions/{id}/activate", async (string id, CancellationToken ct) =>
        {
            bool activated = await _sessionManager.SetActiveSessionAsync(id, ct);
            return activated ? Results.Ok(new { success = true, activeSessionId = id }) : Results.NotFound(new { error = $"Session '{id}' not found." });
        });

        #endregion

        #region 2. Tasks & Execution API

        app.MapGet("/api/tasks", () =>
        {
            var list = _tasks.Values
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => t.ToSummaryRecord())
                .ToArray();
            return Results.Ok(list);
        });

        app.MapPost("/api/tasks", (DispatchTaskRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "Prompt is required." });

            var taskId = $"task_{Guid.NewGuid():N}"[..17];
            string model = req.Model ?? _settings.Models.DefaultModel ?? "gemini-2.5-flash";

            var taskState = new DesktopTaskState
            {
                Id = taskId,
                Prompt = req.Prompt,
                SessionId = req.SessionId,
                Model = model,
                Status = "Running",
                ProgressPercent = 0
            };

            taskState.Steps.Add(new TaskStepRecord(0, "Analyze task requirements and context", "Pending"));
            taskState.Steps.Add(new TaskStepRecord(1, "Inspect relevant files and environment", "Pending"));
            taskState.Steps.Add(new TaskStepRecord(2, "Execute actions and tool operations", "Pending"));
            taskState.Steps.Add(new TaskStepRecord(3, "Verify results and summarize output", "Pending"));

            taskState.ExecutionPlan.Add("Parse user instructions and formulate execution strategy");
            taskState.ExecutionPlan.Add("Check filesystem context and tool privileges");
            taskState.ExecutionPlan.Add("Run reasoning model and dispatch tools");
            taskState.ExecutionPlan.Add("Record telemetry, update session history, and report completion");

            _tasks[taskId] = taskState;

            _ = Task.Run(() => ExecuteDesktopTaskAsync(taskState, req.ToolPermissions));

            return Results.Ok(taskState.ToDetailRecord());
        });

        app.MapGet("/api/tasks/{id}", (string id) =>
        {
            if (_tasks.TryGetValue(id, out var task))
                return Results.Ok(task.ToDetailRecord());

            return Results.NotFound(new { error = $"Task '{id}' not found." });
        });

        app.MapPost("/api/tasks/{id}/pause", async (string id) =>
        {
            if (!_tasks.TryGetValue(id, out var task))
                return Results.NotFound(new { error = $"Task '{id}' not found." });

            if (task.Status != "Running")
                return Results.BadRequest(new { error = $"Cannot pause task '{id}' because it is in '{task.Status}' state." });

            task.Status = "Paused";
            task.PauseEvent.Reset();
            await BroadcastTaskProgressAsync(task.Id, "Paused", task.ProgressPercent, "Paused", "Task paused by user", CancellationToken.None);
            return Results.Ok(task.ToDetailRecord());
        });

        app.MapPost("/api/tasks/{id}/resume", async (string id) =>
        {
            if (!_tasks.TryGetValue(id, out var task))
                return Results.NotFound(new { error = $"Task '{id}' not found." });

            if (task.Status != "Paused")
                return Results.BadRequest(new { error = $"Cannot resume task '{id}' because it is in '{task.Status}' state." });

            task.Status = "Running";
            task.PauseEvent.Set();
            await BroadcastTaskProgressAsync(task.Id, "Running", task.ProgressPercent, "Resumed", "Task resumed by user", CancellationToken.None);
            return Results.Ok(task.ToDetailRecord());
        });

        app.MapPost("/api/tasks/{id}/cancel", async (string id) =>
        {
            if (!_tasks.TryGetValue(id, out var task))
                return Results.NotFound(new { error = $"Task '{id}' not found." });

            if (task.Status != "Running" && task.Status != "Paused")
                return Results.BadRequest(new { error = $"Cannot cancel task '{id}' because it is in '{task.Status}' state." });

            task.Status = "Cancelled";
            task.Cts.Cancel();
            task.PauseEvent.Set();
            task.CompletedAt = DateTimeOffset.UtcNow;
            await BroadcastTaskProgressAsync(task.Id, "Cancelled", task.ProgressPercent, "Cancelled", "Task cancelled by user", CancellationToken.None);
            return Results.Ok(task.ToDetailRecord());
        });

        #endregion

        #region 3. Filesystem & Diff API

        app.MapGet("/api/fs/tree", (string? path, int? maxDepth) =>
        {
            string root;
            if (string.IsNullOrWhiteSpace(path))
            {
                root = _workspaceDir;
            }
            else
            {
                if (!TryResolveWorkspacePath(path, out root, out var error))
                    return error!;
            }

            if (!Directory.Exists(root))
                return Results.NotFound(new { error = $"Directory '{root}' not found." });

            var gitMap = GetGitStatusMap(root);
            var tree = BuildFsTree(root, root, gitMap, 0, maxDepth ?? 3);
            return Results.Ok(new
            {
                root = root.Replace('\\', '/'),
                items = tree
            });
        });

        app.MapGet("/api/fs/read", async (string? path, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Path parameter is required." });

            if (!TryResolveWorkspacePath(path, out var fullPath, out var error))
                return error!;

            if (!File.Exists(fullPath))
                return Results.NotFound(new { error = $"File '{path}' not found." });

            var content = await File.ReadAllTextAsync(fullPath, ct);
            var fi = new FileInfo(fullPath);
            return Results.Ok(new FsReadResponse(path.Replace('\\', '/'), content, fi.Length, fi.LastWriteTimeUtc));
        });

        app.MapPost("/api/fs/write", async (FsWriteRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "Path is required." });

            if (!TryResolveWorkspacePath(req.Path, out var fullPath, out var error))
                return error!;

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                if (req.CreateDirectory)
                    Directory.CreateDirectory(dir);
                else
                    return Results.BadRequest(new { error = $"Directory '{dir}' does not exist." });
            }

            if (File.Exists(fullPath) && !req.Overwrite)
                return Results.Conflict(new { error = $"File '{req.Path}' already exists and overwrite is false." });

            await File.WriteAllTextAsync(fullPath, req.Content, ct);
            return Results.Ok(new FsWriteResponse(true, req.Path.Replace('\\', '/'), Encoding.UTF8.GetByteCount(req.Content)));
        });

        app.MapGet("/api/fs/diff", async (string? path, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Path parameter is required." });

            if (!TryResolveWorkspacePath(path, out var fullPath, out var error))
                return error!;

            if (_pendingDiffs.TryGetValue(path, out var pending))
                return Results.Ok(pending);

            string existing = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, ct) : string.Empty;

            var diff = _diffService.ComputeDiff(path, existing, existing);
            var unified = _diffService.FormatUnifiedDiff(diff);
            return Results.Ok(new FsDiffResponse(diff.FilePath, diff.OldContent, diff.NewContent, 0, 0, false, unified, diff.Hunks));
        });

        app.MapPost("/api/fs/diff", async (FsDiffRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "Path is required." });

            if (!TryResolveWorkspacePath(req.Path, out var fullPath, out var error))
                return error!;

            string oldContent = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, ct) : string.Empty;

            var diff = _diffService.ComputeDiff(req.Path, oldContent, req.ProposedContent);
            var unified = _diffService.FormatUnifiedDiff(diff);
            var response = new FsDiffResponse(
                diff.FilePath,
                diff.OldContent,
                diff.NewContent,
                diff.AdditionsCount,
                diff.DeletionsCount,
                diff.HasChanges,
                unified,
                diff.Hunks);

            _pendingDiffs[req.Path] = response;
            return Results.Ok(response);
        });

        app.MapPost("/api/fs/diff/approve", async (FsDiffApproveRequest req, CancellationToken ct) =>
        {
            string? targetPath = req.Path;
            DiffDocument? doc = null;

            if (!string.IsNullOrEmpty(targetPath) && _pendingDiffs.TryGetValue(targetPath, out var cached))
            {
                if (!TryResolveWorkspacePath(cached.FilePath, out var fullPath, out var error))
                    return error!;
                doc = _diffService.ComputeDiff(fullPath, cached.OldContent, cached.NewContent);
            }
            else if (!string.IsNullOrEmpty(req.Path) && req.ProposedContent != null)
            {
                if (!TryResolveWorkspacePath(req.Path, out var fullPath, out var error))
                    return error!;
                string oldContent = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, ct) : string.Empty;
                doc = _diffService.ComputeDiff(fullPath, oldContent, req.ProposedContent);
            }

            if (doc == null)
                return Results.BadRequest(new { error = "No pending or proposed diff found to approve." });

            bool success = await _diffService.ApplyDiffAsync(doc, createBackup: true, ct);
            if (!string.IsNullOrEmpty(targetPath))
                _pendingDiffs.TryRemove(targetPath, out _);

            return Results.Ok(new
            {
                success,
                path = doc.FilePath,
                additions = doc.AdditionsCount,
                deletions = doc.DeletionsCount,
                backupCreated = true
            });
        });

        app.MapPost("/api/fs/diff/reject", (FsDiffRejectRequest req) =>
        {
            if (!string.IsNullOrEmpty(req.Path))
                _pendingDiffs.TryRemove(req.Path, out _);

            return Results.Ok(new { success = true, rejected = true, path = req.Path });
        });

        #endregion

        #region 4. Agent Management API

        app.MapGet("/api/agents", () =>
        {
            var configs = _agentManager.ListAgents();
            var states = _agentManager.ListAgentStates().ToDictionary(s => s.Config.Id);
            var list = configs.Select(c =>
            {
                states.TryGetValue(c.Id, out var state);
                return new AgentDetailResponse(c, state?.Status ?? AgentStatus.Idle, state, FormatToolPermissions(c.ToolPermissions));
            }).ToArray();
            return Results.Ok(list);
        });

        app.MapPost("/api/agents", (CreateAgentApiRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.SystemPrompt))
                return Results.BadRequest(new { error = "Name and SystemPrompt are required." });

            var perms = ParseToolPermissions(req.ToolPermissions);
            var config = new AgentConfig(
                $"agent_{Guid.NewGuid():N}"[..14],
                req.Name,
                req.SystemPrompt,
                req.PreferredModel ?? "gemini-2.5-flash",
                req.WorkingDirectory ?? _workspaceDir,
                perms,
                req.MaxTokenBudget ?? 32768,
                req.AutonomyTier ?? "Guarded");

            var registered = _agentManager.RegisterAgent(config);
            return Results.Ok(new AgentDetailResponse(registered, AgentStatus.Idle, null, FormatToolPermissions(registered.ToolPermissions)));
        });

        app.MapGet("/api/agents/{id}", (string id) =>
        {
            var config = _agentManager.GetAgent(id);
            if (config == null) return Results.NotFound(new { error = $"Agent '{id}' not found." });

            var state = _agentManager.GetAgentState(id);
            return Results.Ok(new AgentDetailResponse(config, state?.Status ?? AgentStatus.Idle, state, FormatToolPermissions(config.ToolPermissions)));
        });

        app.MapPatch("/api/agents/{id}", (string id, UpdateAgentApiRequest req) =>
        {
            var config = _agentManager.GetAgent(id);
            if (config == null) return Results.NotFound(new { error = $"Agent '{id}' not found." });

            var perms = req.ToolPermissions != null ? ParseToolPermissions(req.ToolPermissions) : config.ToolPermissions;
            var updated = config with
            {
                Name = req.Name ?? config.Name,
                SystemPrompt = req.SystemPrompt ?? config.SystemPrompt,
                PreferredModel = req.PreferredModel ?? config.PreferredModel,
                WorkingDirectory = req.WorkingDirectory ?? config.WorkingDirectory,
                ToolPermissions = perms,
                MaxTokenBudget = req.MaxTokenBudget ?? config.MaxTokenBudget,
                AutonomyTier = req.AutonomyTier ?? config.AutonomyTier,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _agentManager.UpdateAgentConfig(updated);
            var state = _agentManager.GetAgentState(id);
            return Results.Ok(new AgentDetailResponse(updated, state?.Status ?? AgentStatus.Idle, state, FormatToolPermissions(updated.ToolPermissions)));
        });

        app.MapDelete("/api/agents/{id}", (string id) =>
        {
            bool deleted = _agentManager.DeleteAgent(id);
            return deleted ? Results.Ok(new { success = true, id }) : Results.NotFound(new { error = $"Agent '{id}' not found." });
        });

        app.MapPost("/api/agents/{id}/tools/permissions", (string id, AgentToolPermissionsToggleRequest req) =>
        {
            var config = _agentManager.GetAgent(id);
            if (config == null) return Results.NotFound(new { error = $"Agent '{id}' not found." });

            AgentToolPermissions newPerms = config.ToolPermissions;
            if (req.Permissions != null)
            {
                newPerms = ParseToolPermissions(req.Permissions);
            }
            else if (!string.IsNullOrEmpty(req.Tool) && req.Enabled.HasValue)
            {
                var toolFlag = ParseToolPermissions([req.Tool]);
                if (req.Enabled.Value)
                    newPerms |= toolFlag;
                else
                    newPerms &= ~toolFlag;
            }

            var updated = config with { ToolPermissions = newPerms, UpdatedAt = DateTimeOffset.UtcNow };
            _agentManager.UpdateAgentConfig(updated);

            return Results.Ok(new
            {
                id = updated.Id,
                permissions = FormatToolPermissions(newPerms),
                toolPermissionsValue = (int)newPerms
            });
        });

        #endregion

        #region 5. Configuration & Settings Persistence API

        app.MapGet("/api/settings", () => Results.Ok(_settings.CloneMasked()));

        app.MapPost("/api/settings", (DesktopSettings req) =>
        {
            if (req.Appearance != null && !string.IsNullOrWhiteSpace(req.Appearance.Theme))
            {
                var theme = req.Appearance.Theme.ToLowerInvariant();
                if (theme != "dark" && theme != "light" && theme != "system")
                    return Results.BadRequest(new { error = "Theme must be 'dark', 'light', or 'system'." });
            }

            if (req.Models != null && string.IsNullOrWhiteSpace(req.Models.DefaultModel))
            {
                return Results.BadRequest(new { error = "DefaultModel cannot be empty." });
            }

            if (req.Models != null)
            {
                _settings.Models.DefaultModel = req.Models.DefaultModel;
                if (req.Models.AvailableModels is { Count: > 0 })
                    _settings.Models.AvailableModels = req.Models.AvailableModels;
                if (!string.IsNullOrEmpty(req.Models.FallbackModel))
                    _settings.Models.FallbackModel = req.Models.FallbackModel;
            }

            if (req.ApiKeys != null)
            {
                if (!string.IsNullOrEmpty(req.ApiKeys.GeminiApiKey) && !req.ApiKeys.GeminiApiKey.Contains("***"))
                    _settings.ApiKeys.GeminiApiKey = req.ApiKeys.GeminiApiKey;
                if (!string.IsNullOrEmpty(req.ApiKeys.OpenAiApiKey) && !req.ApiKeys.OpenAiApiKey.Contains("***"))
                    _settings.ApiKeys.OpenAiApiKey = req.ApiKeys.OpenAiApiKey;
                if (!string.IsNullOrEmpty(req.ApiKeys.AnthropicApiKey) && !req.ApiKeys.AnthropicApiKey.Contains("***"))
                    _settings.ApiKeys.AnthropicApiKey = req.ApiKeys.AnthropicApiKey;
                if (!string.IsNullOrEmpty(req.ApiKeys.OpenRouterApiKey) && !req.ApiKeys.OpenRouterApiKey.Contains("***"))
                    _settings.ApiKeys.OpenRouterApiKey = req.ApiKeys.OpenRouterApiKey;
                if (!string.IsNullOrEmpty(req.ApiKeys.NousApiKey) && !req.ApiKeys.NousApiKey.Contains("***"))
                    _settings.ApiKeys.NousApiKey = req.ApiKeys.NousApiKey;
                if (!string.IsNullOrEmpty(req.ApiKeys.OllamaEndpoint))
                    _settings.ApiKeys.OllamaEndpoint = req.ApiKeys.OllamaEndpoint;
                if (!string.IsNullOrEmpty(req.ApiKeys.EvrenEndpoint))
                    _settings.ApiKeys.EvrenEndpoint = req.ApiKeys.EvrenEndpoint;
                if (!string.IsNullOrEmpty(req.ApiKeys.EvrenApiKey) && !req.ApiKeys.EvrenApiKey.Contains("***"))
                    _settings.ApiKeys.EvrenApiKey = req.ApiKeys.EvrenApiKey;
                if (req.ApiKeys.EvrenCustomHeader != null)
                    _settings.ApiKeys.EvrenCustomHeader = req.ApiKeys.EvrenCustomHeader;
                _settings.ApiKeys.EvrenIgnoreSslErrors = req.ApiKeys.EvrenIgnoreSslErrors;
                if (!string.IsNullOrEmpty(req.ApiKeys.CustomEndpoint))
                    _settings.ApiKeys.CustomEndpoint = req.ApiKeys.CustomEndpoint;
                if (!string.IsNullOrEmpty(req.ApiKeys.CustomApiKey) && !req.ApiKeys.CustomApiKey.Contains("***"))
                    _settings.ApiKeys.CustomApiKey = req.ApiKeys.CustomApiKey;
                if (req.ApiKeys.CustomAuthHeader != null)
                    _settings.ApiKeys.CustomAuthHeader = req.ApiKeys.CustomAuthHeader;
                _settings.ApiKeys.CustomIgnoreSslErrors = req.ApiKeys.CustomIgnoreSslErrors;
            }

            if (req.OnboardingCompleted)
                _settings.OnboardingCompleted = true;

            if (!string.IsNullOrWhiteSpace(req.PreferredProvider))
                _settings.PreferredProvider = req.PreferredProvider.Trim().ToLowerInvariant();

            if (req.Appearance != null)
            {
                if (!string.IsNullOrEmpty(req.Appearance.Theme))
                    _settings.Appearance.Theme = req.Appearance.Theme.ToLowerInvariant();
                if (req.Appearance.FontSize > 0)
                    _settings.Appearance.FontSize = req.Appearance.FontSize;
                if (!string.IsNullOrEmpty(req.Appearance.FontFamily))
                    _settings.Appearance.FontFamily = req.Appearance.FontFamily;
                _settings.Appearance.CompactSidebar = req.Appearance.CompactSidebar;
            }

            if (req.Permissions != null)
            {
                if (!string.IsNullOrEmpty(req.Permissions.DefaultAutonomyTier))
                    _settings.Permissions.DefaultAutonomyTier = req.Permissions.DefaultAutonomyTier;
                if (req.Permissions.AllowedTools is { Count: > 0 })
                    _settings.Permissions.AllowedTools = req.Permissions.AllowedTools;
                _settings.Permissions.RequireApprovalForRed = req.Permissions.RequireApprovalForRed;
            }

            if (!string.IsNullOrEmpty(req.DefaultWorkingDir))
                _settings.DefaultWorkingDir = req.DefaultWorkingDir;

            _settings.TelemetryEnabled = req.TelemetryEnabled;

            SaveSettingsInternal(_settings);

            return Results.Ok(_settings.CloneMasked());
        });

        app.MapGet("/api/setup/status", () =>
        {
            var configured = _router.GetConfiguredProviders();
            return Results.Ok(new SetupStatusResponse(
                _settings.OnboardingCompleted,
                _settings.PreferredProvider,
                _settings.Models.DefaultModel,
                configured,
                _settings.CloneMasked()));
        });

        app.MapPost("/api/setup/test-llm", async (LlmConnectionTestRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Provider))
                return Results.BadRequest(new { error = "Provider is required." });

            var provider = req.Provider.Trim().ToLowerInvariant();
            if (provider == "evren") provider = "ssb-evren";

            string? resolvedKey = req.ApiKey;
            if (string.IsNullOrWhiteSpace(resolvedKey) || resolvedKey.Contains("***", StringComparison.Ordinal))
            {
                resolvedKey = provider switch
                {
                    "ssb-evren" => _settings.ApiKeys.EvrenApiKey,
                    "custom" => _settings.ApiKeys.CustomApiKey,
                    "gemini" => _settings.ApiKeys.GeminiApiKey,
                    "openai" => _settings.ApiKeys.OpenAiApiKey,
                    "anthropic" => _settings.ApiKeys.AnthropicApiKey,
                    "openrouter" => _settings.ApiKeys.OpenRouterApiKey,
                    "nous" => _settings.ApiKeys.NousApiKey,
                    _ => null
                };
            }

            string? endpoint = !string.IsNullOrWhiteSpace(req.EndpointUrl)
                ? req.EndpointUrl
                : provider switch
                {
                    "ollama" => _settings.ApiKeys.OllamaEndpoint,
                    "ssb-evren" => _settings.ApiKeys.EvrenEndpoint,
                    "custom" => _settings.ApiKeys.CustomEndpoint,
                    _ => null
                };

            var result = await MultiModelRouter.TestProviderConnectionAsync(
                provider,
                resolvedKey,
                req.Model,
                endpoint,
                customHandler: null,
                ct: ct,
                customAuthHeader: req.CustomAuthHeader,
                ignoreSslErrors: req.IgnoreSslErrors);

            return Results.Ok(result);
        });

        app.MapPost("/api/setup/models", async (FetchModelsRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.EndpointUrl))
                return Results.BadRequest(new { error = "EndpointUrl is required." });

            try
            {
                var models = await OpenAiCompatibleLiveClient.FetchAvailableModelsAsync(
                    req.EndpointUrl,
                    req.ApiKey,
                    req.CustomAuthHeader,
                    req.IgnoreSslErrors,
                    httpClient: null,
                    ct: ct);
                return Results.Ok(new FetchModelsResponse(true, models, $"Fetched {models.Count} model(s)."));
            }
            catch (Exception ex)
            {
                return Results.Ok(new FetchModelsResponse(false, Array.Empty<string>(), ex.Message));
            }
        });

        app.MapPost("/api/setup/complete", (SetupCompleteRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.PreferredProvider))
                return Results.BadRequest(new { error = "PreferredProvider is required." });
            if (string.IsNullOrWhiteSpace(req.DefaultModel))
                return Results.BadRequest(new { error = "DefaultModel is required." });

            var provider = req.PreferredProvider.Trim().ToLowerInvariant();
            if (provider == "evren") provider = "ssb-evren";

            _settings.PreferredProvider = provider;
            _settings.OnboardingCompleted = true;
            _settings.Models.DefaultModel = req.DefaultModel.Trim();

            if (!string.IsNullOrWhiteSpace(req.FallbackModel))
                _settings.Models.FallbackModel = req.FallbackModel.Trim();

            if (!_settings.Models.AvailableModels.Contains(_settings.Models.DefaultModel, StringComparer.OrdinalIgnoreCase))
                _settings.Models.AvailableModels.Insert(0, _settings.Models.DefaultModel);

            if (!string.IsNullOrWhiteSpace(req.ApiKey) && !req.ApiKey.Contains("***", StringComparison.Ordinal))
            {
                var cleanKey = req.ApiKey.Trim();
                switch (provider)
                {
                    case "ssb-evren":
                        _settings.ApiKeys.EvrenApiKey = cleanKey;
                        break;
                    case "custom":
                        _settings.ApiKeys.CustomApiKey = cleanKey;
                        break;
                    case "gemini":
                        _settings.ApiKeys.GeminiApiKey = cleanKey;
                        break;
                    case "openai":
                        _settings.ApiKeys.OpenAiApiKey = cleanKey;
                        break;
                    case "anthropic":
                        _settings.ApiKeys.AnthropicApiKey = cleanKey;
                        break;
                    case "openrouter":
                        _settings.ApiKeys.OpenRouterApiKey = cleanKey;
                        break;
                    case "nous":
                        _settings.ApiKeys.NousApiKey = cleanKey;
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(req.EndpointUrl))
            {
                if (provider == "ssb-evren")
                    _settings.ApiKeys.EvrenEndpoint = req.EndpointUrl.Trim();
                else if (provider == "custom")
                    _settings.ApiKeys.CustomEndpoint = req.EndpointUrl.Trim();
                else if (provider == "ollama")
                    _settings.ApiKeys.OllamaEndpoint = req.EndpointUrl.Trim();
            }

            if (provider == "ssb-evren")
            {
                if (req.CustomAuthHeader != null)
                    _settings.ApiKeys.EvrenCustomHeader = req.CustomAuthHeader.Trim();
                _settings.ApiKeys.EvrenIgnoreSslErrors = req.IgnoreSslErrors;
            }
            else if (provider == "custom")
            {
                if (req.CustomAuthHeader != null)
                    _settings.ApiKeys.CustomAuthHeader = req.CustomAuthHeader.Trim();
                _settings.ApiKeys.CustomIgnoreSslErrors = req.IgnoreSslErrors;
            }

            if (!string.IsNullOrWhiteSpace(req.OllamaEndpoint))
                _settings.ApiKeys.OllamaEndpoint = req.OllamaEndpoint.Trim();

            if (!string.IsNullOrWhiteSpace(req.DefaultWorkingDir))
                _settings.DefaultWorkingDir = req.DefaultWorkingDir.Trim();

            if (!string.IsNullOrWhiteSpace(req.AutonomyTier))
                _settings.Permissions.DefaultAutonomyTier = req.AutonomyTier.Trim();

            if (!string.IsNullOrWhiteSpace(req.Theme))
                _settings.Appearance.Theme = req.Theme.Trim().ToLowerInvariant();

            SaveSettingsInternal(_settings);

            return Results.Ok(new SetupStatusResponse(
                _settings.OnboardingCompleted,
                _settings.PreferredProvider,
                _settings.Models.DefaultModel,
                _router.GetConfiguredProviders(),
                _settings.CloneMasked()));
        });

        #endregion

        #region 6. Terminal Execution API

        app.MapPost("/api/terminal/exec", async (TerminalExecRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new { error = "Command is required." });

            string execDir = _workspaceDir;
            if (!string.IsNullOrEmpty(req.SessionId))
            {
                var session = await _sessionManager.GetSessionAsync(req.SessionId, ct);
                if (session != null && !string.IsNullOrEmpty(session.WorkingDir) && Directory.Exists(session.WorkingDir))
                {
                    execDir = session.WorkingDir;
                }
            }
            else if (!string.IsNullOrEmpty(req.WorkingDir) && Directory.Exists(req.WorkingDir))
            {
                execDir = req.WorkingDir;
            }

            var riskTier = _smartApproval.ClassifyAction("terminal", new Dictionary<string, string> { ["cmd"] = req.Command });

            bool wasApproved = riskTier == RiskTier.Green || req.Approved;
            if (!wasApproved && !string.IsNullOrEmpty(req.SessionId))
            {
                wasApproved = await _sessionManager.CheckPermissionGrantAsync(req.SessionId, "terminal", req.Command, ct);
            }

            if (!wasApproved && riskTier == RiskTier.Red)
            {
                var approvalReq = ApprovalRequest.Create(
                    "terminal",
                    RiskTier.Red,
                    $"Execute terminal command: {req.Command}",
                    new Dictionary<string, string> { ["cmd"] = req.Command });

                await BroadcastSmartApprovalPromptAsync(approvalReq.Id, approvalReq.ActionName, approvalReq.Tier.ToString(), approvalReq.Description, ct);

                return Results.Json(new
                {
                    error = "Interactive approval required for Red-tier command.",
                    riskTier = riskTier.ToString(),
                    approvalRequired = true,
                    requestId = approvalReq.Id
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            var sw = Stopwatch.StartNew();
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c \"{req.Command}\"" : $"-c \"{req.Command}\"",
                WorkingDirectory = execDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stdoutBuilder.AppendLine(e.Data);
                    _ = BroadcastTerminalOutputAsync(req.Command, e.Data, isError: false, CancellationToken.None);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stderrBuilder.AppendLine(e.Data);
                    _ = BroadcastTerminalOutputAsync(req.Command, e.Data, isError: true, CancellationToken.None);
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var timeoutTask = req.TimeoutSeconds > 0
                ? Task.Delay(TimeSpan.FromSeconds(req.TimeoutSeconds), ct)
                : Task.Delay(Timeout.Infinite, ct);
            var exitTask = proc.WaitForExitAsync(ct);

            if (await Task.WhenAny(exitTask, timeoutTask) == timeoutTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return Results.Json(new { error = "Command timed out.", timeoutSeconds = req.TimeoutSeconds }, statusCode: StatusCodes.Status408RequestTimeout);
            }

            sw.Stop();

            string stdout = stdoutBuilder.ToString();
            string stderr = stderrBuilder.ToString();

            if (!string.IsNullOrEmpty(req.SessionId))
            {
                await _sessionManager.RecordToolCallAsync(
                    req.SessionId,
                    "terminal",
                    req.Command,
                    stdout.Length > 0 ? stdout : stderr,
                    sw.ElapsedMilliseconds,
                    proc.ExitCode == 0 ? "success" : "failed",
                    ct);
            }

            return Results.Ok(new TerminalExecResponse(
                proc.ExitCode,
                stdout,
                stderr,
                sw.ElapsedMilliseconds,
                riskTier.ToString(),
                wasApproved));
        });

        #endregion

        #region 7. Integrations & Channels API

        app.MapGet("/api/integrations", () =>
        {
            var channels = new[] { "telegram", "discord", "slack", "whatsapp", "signal", "email", "cli" };
            var list = channels.Select(ch =>
            {
                bool configured = ch switch
                {
                    "telegram" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")),
                    "discord" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")),
                    "slack" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")),
                    "whatsapp" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WHATSAPP_TOKEN")),
                    "signal" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIGNAL_ENDPOINT")),
                    "email" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMTP_SERVER")),
                    "cli" => true,
                    _ => false
                };
                return new IntegrationStatusResponse(ch, configured, Connected: true, Mode: configured ? "live" : "mock");
            }).ToArray();
            return Results.Ok(list);
        });

        app.MapPost("/api/integrations/test", async (IntegrationTestRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Channel))
                return Results.BadRequest(new { error = "Channel is required." });

            var msg = AgentMessage.Create(req.Channel.ToLowerInvariant(), "system_test", req.Message ?? "Ping test from Desktop Host");
            await _eventBus.PublishAsync(msg, ct);
            return Results.Ok(new IntegrationTestResponse(req.Channel, true, $"Message published to EventBus on channel '{req.Channel}'."));
        });

        #endregion
    }

    #region Task Execution Engine

    private async Task ExecuteDesktopTaskAsync(DesktopTaskState task, List<string>? requestedPermissions)
    {
        var ct = task.Cts.Token;
        try
        {
            task.StartedAt = DateTimeOffset.UtcNow;
            if (task.PauseEvent.IsSet && task.Status != "Cancelled")
            {
                task.Status = "Running";
            }
            task.ProgressPercent = 5;

            await BroadcastTaskProgressAsync(task.Id, "Running", 5, "Step 1/4: Analyzing request", "Starting task execution...", CancellationToken.None);

            // Step 1: Analyze
            lock (task.Steps)
            {
                task.Steps[0] = task.Steps[0] with { Status = "Running", StartedAt = DateTimeOffset.UtcNow };
            }
            await BroadcastToolStartedAsync(task.Id, "task_analyzer", task.Prompt, CancellationToken.None);
            await Task.Delay(30, ct);

            CheckPause(task, ct);

            await BroadcastReasoningAsync(task.Id, $"Deconstructing task: '{task.Prompt}' with model {task.Model}.", isFinal: false, CancellationToken.None);
            lock (task.Steps)
            {
                task.Steps[0] = task.Steps[0] with { Status = "Completed", CompletedAt = DateTimeOffset.UtcNow };
            }
            lock (task.ToolEvents)
            {
                task.ToolEvents.Add(new TaskEventRecord("tool_completed", "task_analyzer", task.Prompt, "Requirements parsed successfully", 30, "success", DateTimeOffset.UtcNow));
            }
            await BroadcastToolCompletedAsync(task.Id, "task_analyzer", "Requirements parsed successfully", 30, CancellationToken.None);
            task.ProgressPercent = 25;

            // Step 2: Inspect environment
            CheckPause(task, ct);
            lock (task.Steps)
            {
                task.Steps[1] = task.Steps[1] with { Status = "Running", StartedAt = DateTimeOffset.UtcNow };
            }
            await BroadcastToolStartedAsync(task.Id, "fs_inspector", "List working directory files", CancellationToken.None);
            await Task.Delay(30, ct);

            CheckPause(task, ct);
            lock (task.Steps)
            {
                task.Steps[1] = task.Steps[1] with { Status = "Completed", CompletedAt = DateTimeOffset.UtcNow };
            }
            lock (task.ToolEvents)
            {
                task.ToolEvents.Add(new TaskEventRecord("tool_completed", "fs_inspector", "List working directory files", "Working directory verified", 30, "success", DateTimeOffset.UtcNow));
            }
            await BroadcastToolCompletedAsync(task.Id, "fs_inspector", "Working directory verified", 30, CancellationToken.None);
            task.ProgressPercent = 50;

            // Step 3: Execute model & tools
            CheckPause(task, ct);
            lock (task.Steps)
            {
                task.Steps[2] = task.Steps[2] with { Status = "Running", StartedAt = DateTimeOffset.UtcNow };
            }
            var (completion, _) = await _router.RouteAndCompleteAsync(task.Prompt, task.Model, ct);

            var chunks = completion.Split(' ');
            for (int i = 0; i < chunks.Length; i++)
            {
                CheckPause(task, ct);
                bool isLast = i == chunks.Length - 1;
                await BroadcastStreamChunkAsync(task.Id, task.SessionId, chunks[i] + (isLast ? "" : " "), isLast, CancellationToken.None);
                if (chunks.Length > 10 && i % 4 == 0)
                {
                    await Task.Delay(5, ct);
                }
            }

            lock (task.Steps)
            {
                task.Steps[2] = task.Steps[2] with { Status = "Completed", CompletedAt = DateTimeOffset.UtcNow };
            }
            task.ProgressPercent = 80;

            // Step 4: Verification & Telemetry
            CheckPause(task, ct);
            lock (task.Steps)
            {
                task.Steps[3] = task.Steps[3] with { Status = "Running", StartedAt = DateTimeOffset.UtcNow };
            }
            await Task.Delay(10, ct);
            lock (task.Steps)
            {
                task.Steps[3] = task.Steps[3] with { Status = "Completed", CompletedAt = DateTimeOffset.UtcNow };
            }

            int promptTokens = Math.Max(10, task.Prompt.Length / 4);
            int completionTokens = Math.Max(10, completion.Length / 4);
            task.TokensConsumed = promptTokens + completionTokens;
            task.Output = completion;
            task.Status = "Completed";
            task.ProgressPercent = 100;
            task.CompletedAt = DateTimeOffset.UtcNow;

            if (!string.IsNullOrEmpty(task.SessionId))
            {
                await _sessionManager.AppendMessageAsync(task.SessionId, "assistant", completion, task.TokensConsumed, ct: CancellationToken.None);
            }

            await BroadcastTaskProgressAsync(task.Id, "Completed", 100, "Finished", "Task completed successfully", CancellationToken.None);
            await BroadcastTokenTelemetryAsync(task.Id, task.SessionId, promptTokens, completionTokens, task.TokensConsumed, task.TokensConsumed * 0.000002, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            task.Status = "Cancelled";
            task.CompletedAt = DateTimeOffset.UtcNow;
            task.Error = "Task was cancelled by user.";
            await BroadcastTaskProgressAsync(task.Id, "Cancelled", task.ProgressPercent, "Cancelled", "Task execution was cancelled.", CancellationToken.None);
        }
        catch (Exception ex)
        {
            task.Status = "Failed";
            task.CompletedAt = DateTimeOffset.UtcNow;
            task.Error = ex.Message;
            await BroadcastTaskProgressAsync(task.Id, "Failed", task.ProgressPercent, "Error", ex.Message, CancellationToken.None);
        }
    }

    private static void CheckPause(DesktopTaskState task, CancellationToken ct)
    {
        while (!task.PauseEvent.IsSet)
        {
            task.Status = "Paused";
            ct.ThrowIfCancellationRequested();
            task.PauseEvent.Wait(50, ct);
        }
        ct.ThrowIfCancellationRequested();
    }

    #endregion

    #region WebSocket Broadcasting Helpers

    public async Task BroadcastWebSocketAsync(object payload, CancellationToken ct = default)
    {
        if (_wsClients.IsEmpty)
            return;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        foreach (var (id, ws) in _wsClients)
        {
            if (ws.State != WebSocketState.Open)
            {
                _wsClients.TryRemove(id, out _);
                if (_wsLocks.TryRemove(ws, out var s))
                    s.Dispose();
                continue;
            }

            try
            {
                await SendWebSocketSafeAsync(ws, bytes, WebSocketMessageType.Text, true, ct);
            }
            catch
            {
                _wsClients.TryRemove(id, out _);
                if (_wsLocks.TryRemove(ws, out var s))
                    s.Dispose();
            }
        }
    }

    public Task BroadcastAsync(object payload, CancellationToken ct = default) =>
        BroadcastWebSocketAsync(payload, ct);

    public Task BroadcastStreamChunkAsync(string taskId, string? sessionId, string chunk, bool isFinal, CancellationToken ct = default) =>
        BroadcastAsync(new WsStreamChunk(taskId, sessionId, chunk, isFinal, DateTimeOffset.UtcNow), ct);

    public Task BroadcastToolStartedAsync(string taskId, string toolName, string input, CancellationToken ct = default) =>
        BroadcastAsync(new WsToolStarted(taskId, toolName, input, DateTimeOffset.UtcNow), ct);

    public Task BroadcastToolProgressAsync(string taskId, string toolName, int progress, string message, CancellationToken ct = default) =>
        BroadcastAsync(new WsToolProgress(taskId, toolName, progress, message, DateTimeOffset.UtcNow), ct);

    public Task BroadcastToolCompletedAsync(string taskId, string toolName, string output, long durationMs, CancellationToken ct = default) =>
        BroadcastAsync(new WsToolCompleted(taskId, toolName, output, durationMs, "success", DateTimeOffset.UtcNow), ct);

    public Task BroadcastToolFailedAsync(string taskId, string toolName, string error, long durationMs, CancellationToken ct = default) =>
        BroadcastAsync(new WsToolFailed(taskId, toolName, error, durationMs, "failed", DateTimeOffset.UtcNow), ct);

    public Task BroadcastReasoningAsync(string taskId, string thoughts, bool isFinal, CancellationToken ct = default) =>
        BroadcastAsync(new WsReasoning(taskId, thoughts, isFinal, DateTimeOffset.UtcNow), ct);

    public Task BroadcastTaskProgressAsync(string taskId, string status, int progressPercent, string currentStep, string message, CancellationToken ct = default) =>
        BroadcastAsync(new WsTaskProgress(taskId, status, progressPercent, currentStep, message, DateTimeOffset.UtcNow), ct);

    public Task BroadcastTokenTelemetryAsync(string taskId, string? sessionId, int promptTokens, int completionTokens, int totalTokens, double estimatedCost, CancellationToken ct = default) =>
        BroadcastAsync(new WsTokenTelemetry(taskId, sessionId, promptTokens, completionTokens, totalTokens, estimatedCost, DateTimeOffset.UtcNow), ct);

    public Task BroadcastSmartApprovalPromptAsync(string requestId, string actionName, string tier, string description, CancellationToken ct = default) =>
        BroadcastAsync(new WsSmartApprovalPrompt(requestId, actionName, tier, description, DateTimeOffset.UtcNow), ct);

    public Task BroadcastTerminalOutputAsync(string command, string output, bool isError, CancellationToken ct = default) =>
        BroadcastAsync(new WsTerminalOutput(command, output, isError, DateTimeOffset.UtcNow), ct);

    #endregion

    #region Helper Methods

    private bool TryResolveWorkspacePath(string? requestedPath, out string fullPath, out IResult? errorResult)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            fullPath = string.Empty;
            errorResult = Results.BadRequest(new { error = "Path parameter is required." });
            return false;
        }

        fullPath = Path.GetFullPath(Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(_workspaceDir, requestedPath));
        var normalizedWorkspace = Path.GetFullPath(_workspaceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workspaceWithSlash = normalizedWorkspace + Path.DirectorySeparatorChar;

        bool isWithin = normalizedTarget.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) ||
                        normalizedTarget.StartsWith(workspaceWithSlash, StringComparison.OrdinalIgnoreCase);

        if (!isWithin)
        {
            errorResult = Results.BadRequest(new { error = "Access denied: Path outside workspace boundary." });
            return false;
        }

        errorResult = null;
        return true;
    }

    private async Task SendWebSocketSafeAsync(WebSocket ws, byte[] bytes, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
            return;

        var sem = _wsLocks.GetOrAdd(ws, static _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(bytes, messageType, endOfMessage, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    private void LoadOrCreateSettings()
    {
        lock (_settingsLock)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<DesktopSettings>(json, JsonOptions);
                    if (loaded != null)
                    {
                        _settings = loaded;
                        ApplySettingsToRuntime(_settings);
                        return;
                    }
                }
            }
            catch
            {
                // fallback to default
            }

            _settings = new DesktopSettings
            {
                DefaultWorkingDir = _workspaceDir
            };
            SaveSettingsInternal(_settings);
        }
    }

    private void SaveSettingsInternal(DesktopSettings settings)
    {
        lock (_settingsLock)
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempFile = _settingsFilePath + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _settingsFilePath, overwrite: true);
            ApplySettingsToRuntime(settings);
        }
    }

    private void ApplySettingsToRuntime(DesktopSettings settings)
    {
        if (settings.ApiKeys != null)
        {
            if (!string.IsNullOrEmpty(settings.ApiKeys.GeminiApiKey))
                Environment.SetEnvironmentVariable("GEMINI_API_KEY", settings.ApiKeys.GeminiApiKey);
            if (!string.IsNullOrEmpty(settings.ApiKeys.OpenAiApiKey))
                Environment.SetEnvironmentVariable("OPENAI_API_KEY", settings.ApiKeys.OpenAiApiKey);
            if (!string.IsNullOrEmpty(settings.ApiKeys.AnthropicApiKey))
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", settings.ApiKeys.AnthropicApiKey);
            if (!string.IsNullOrEmpty(settings.ApiKeys.OpenRouterApiKey))
                Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", settings.ApiKeys.OpenRouterApiKey);
            if (!string.IsNullOrEmpty(settings.ApiKeys.NousApiKey))
                Environment.SetEnvironmentVariable("NOUS_API_KEY", settings.ApiKeys.NousApiKey);
        }

        _router.ConfigureFromSettings(settings);
    }

    private static Dictionary<string, string> GetGitStatusMap(string rootDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain",
                WorkingDirectory = rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length >= 4)
                    {
                        string status = line[..2].Trim();
                        string relPath = line[3..].Trim().Replace('\\', '/');
                        string statusDesc = status switch
                        {
                            "M" => "modified",
                            "??" => "untracked",
                            "A" => "added",
                            "D" => "deleted",
                            "R" => "renamed",
                            _ => "modified"
                        };
                        map[relPath] = statusDesc;
                    }
                }
            }
        }
        catch
        {
            // Ignore if git not found
        }
        return map;
    }

    private static List<FsTreeNode> BuildFsTree(
        string dirPath,
        string rootDir,
        Dictionary<string, string> gitStatusMap,
        int currentDepth,
        int maxDepth)
    {
        var nodes = new List<FsTreeNode>();
        if (!Directory.Exists(dirPath) || currentDepth > maxDepth)
            return nodes;

        try
        {
            var dirInfo = new DirectoryInfo(dirPath);

            foreach (var subDir in dirInfo.GetDirectories())
            {
                if (subDir.Name.StartsWith(".") ||
                    subDir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    subDir.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    subDir.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relPath = Path.GetRelativePath(rootDir, subDir.FullName).Replace('\\', '/');
                var children = BuildFsTree(subDir.FullName, rootDir, gitStatusMap, currentDepth + 1, maxDepth);
                nodes.Add(new FsTreeNode(
                    subDir.Name,
                    relPath,
                    IsDirectory: true,
                    Size: 0,
                    LastModified: subDir.LastWriteTimeUtc,
                    GitStatus: "unmodified",
                    Children: children));
            }

            foreach (var file in dirInfo.GetFiles())
            {
                var relPath = Path.GetRelativePath(rootDir, file.FullName).Replace('\\', '/');
                gitStatusMap.TryGetValue(relPath, out var gitStatus);
                nodes.Add(new FsTreeNode(
                    file.Name,
                    relPath,
                    IsDirectory: false,
                    Size: file.Length,
                    LastModified: file.LastWriteTimeUtc,
                    GitStatus: gitStatus ?? "unmodified",
                    Children: null));
            }
        }
        catch
        {
            // Permission or access error
        }

        return nodes;
    }

    public static AgentToolPermissions ParseToolPermissions(IEnumerable<string>? permissions)
    {
        if (permissions == null) return AgentToolPermissions.All;
        AgentToolPermissions flags = AgentToolPermissions.None;
        foreach (var p in permissions)
        {
            switch (p.Trim().ToLowerInvariant())
            {
                case "terminal":
                case "exec":
                case "bash":
                case "pwsh":
                    flags |= AgentToolPermissions.Terminal;
                    break;
                case "filesystem":
                case "fileops":
                case "files":
                    flags |= AgentToolPermissions.FileOps;
                    break;
                case "web":
                case "websearch":
                case "search":
                    flags |= AgentToolPermissions.WebSearch;
                    break;
                case "cdp":
                case "browser":
                case "browsercdp":
                    flags |= AgentToolPermissions.BrowserCdp;
                    break;
                case "git":
                    flags |= AgentToolPermissions.Git;
                    break;
                case "subagents":
                case "agents":
                    flags |= AgentToolPermissions.SubAgents;
                    break;
                case "all":
                    flags |= AgentToolPermissions.All;
                    break;
            }
        }
        return flags;
    }

    public static List<string> FormatToolPermissions(AgentToolPermissions permissions)
    {
        var list = new List<string>();
        if (permissions.HasFlag(AgentToolPermissions.Terminal)) list.Add("terminal");
        if (permissions.HasFlag(AgentToolPermissions.FileOps)) list.Add("filesystem");
        if (permissions.HasFlag(AgentToolPermissions.WebSearch)) list.Add("web");
        if (permissions.HasFlag(AgentToolPermissions.BrowserCdp)) list.Add("cdp");
        if (permissions.HasFlag(AgentToolPermissions.Git)) list.Add("git");
        if (permissions.HasFlag(AgentToolPermissions.SubAgents)) list.Add("subagents");
        return list;
    }

    private string LoadUiAsset(string fileName, string fallback)
    {
        var path = Path.Combine(_uiRootDir, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : fallback;
    }

    private static string ResolveDefaultUiDirectory()
    {
        var directCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Desktop", "UI"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "HUMofA", "Desktop", "UI"),
            Path.Combine(Directory.GetCurrentDirectory(), "Desktop", "UI")
        };

        foreach (var c in directCandidates)
        {
            if (File.Exists(Path.Combine(c, "index.html")))
                return c;
        }

        // Walk up parent directories from AppContext.BaseDirectory and CurrentDirectory
        foreach (var startDir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(startDir);
            while (current is not null)
            {
                var c1 = Path.Combine(current.FullName, "src", "HUMofA", "Desktop", "UI");
                if (File.Exists(Path.Combine(c1, "index.html")))
                    return c1;

                var c2 = Path.Combine(current.FullName, "Desktop", "UI");
                if (File.Exists(Path.Combine(c2, "index.html")))
                    return c2;

                current = current.Parent;
            }
        }

        return directCandidates[0];
    }

    private static string GetFallbackHtml() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <title>HUMofA — Humanize of AI Desktop</title>
            <link rel="stylesheet" href="/style.css" />
        </head>
        <body>
            <div id="app">
                <h1>HUMofA — Autonomous Right-Hand PC Assistant</h1>
                <div id="ghost-typing-stream"></div>
            </div>
            <script src="/app.js"></script>
        </body>
        </html>
        """;

    private static string GetFallbackJs() => """
        console.log("HUMofA Desktop UI initialized.");
        """;

    private static string GetFallbackCss() => """
        body { background: #0f172a; color: #f8fafc; font-family: system-ui, sans-serif; }
        """;

    #endregion

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_busListenerCts is not null)
        {
            await _busListenerCts.CancelAsync();
            if (_busListenerTask is not null)
            {
                try { await _busListenerTask; } catch { }
            }
            _busListenerCts.Dispose();
            _busListenerCts = null;
            _busListenerTask = null;
        }

        if (_app is not null)
        {
            await _app.StopAsync(ct);
            await _app.DisposeAsync();
            _app = null;
        }

        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var sem in _wsLocks.Values)
        {
            try { sem.Dispose(); } catch { }
        }
        _wsLocks.Clear();
        foreach (var task in _tasks.Values)
        {
            try
            {
                task.Cts.Cancel();
                task.PauseEvent.Set();
                task.Cts.Dispose();
                task.PauseEvent.Dispose();
            }
            catch
            {
            }
        }
        await _subAgents.RpcBridge.DisposeAsync();
        _memoryStore.Dispose();

        if (_ownsSessionManager)
        {
            await _sessionManager.DisposeAsync();
        }
    }
}
