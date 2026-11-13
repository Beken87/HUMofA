using System.Text.Json.Serialization;
using HUMofA.Agent;
using HUMofA.Diff;
using HUMofA.Sessions;

namespace HUMofA.Desktop;

#region Session DTOs

public sealed record CreateSessionApiRequest(
    string? Title = null,
    string? Model = "gemini-2.5-flash",
    string? WorkingDir = null,
    string? Instructions = null,
    string? AutonomyTier = AutonomyTiers.Guarded,
    int? MaxTokens = 8192);

public sealed record UpdateSessionApiRequest(
    string? Title = null,
    string? Model = null,
    string? Instructions = null,
    string? WorkingDir = null,
    string? AutonomyTier = null,
    int? MaxTokens = null,
    bool? Pinned = null);

public sealed record SessionDetailResponse(
    SessionRecord Session,
    IReadOnlyList<SessionMessage> Transcript,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<SessionPermissionGrant> Permissions,
    int TotalTokens);

#endregion

#region Task DTOs

public sealed record DispatchTaskRequest(
    string Prompt,
    string? SessionId = null,
    string? Model = null,
    string? WorkingDir = null,
    List<string>? ToolPermissions = null);

public sealed record TaskStepRecord(
    int StepIndex,
    string Title,
    string Status,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? Error = null);

public sealed record TaskEventRecord(
    string EventType,
    string ToolName,
    string Input,
    string Output,
    long DurationMs,
    string Status,
    DateTimeOffset Timestamp);

public sealed record TaskDetailRecord(
    string Id,
    string Prompt,
    string? SessionId,
    string Model,
    string Status,
    int ProgressPercent,
    IReadOnlyList<TaskStepRecord> Steps,
    IReadOnlyList<string> ExecutionPlan,
    IReadOnlyList<TaskEventRecord> ToolEvents,
    int TokensConsumed,
    string? Output,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? Error = null);

public sealed record TaskSummaryRecord(
    string Id,
    string Prompt,
    string? SessionId,
    string Model,
    string Status,
    int ProgressPercent,
    int TokensConsumed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed class DesktopTaskState
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public string? SessionId { get; init; }
    public required string Model { get; set; }
    public string Status { get; set; } = "Pending";
    public int ProgressPercent { get; set; } = 0;
    public List<TaskStepRecord> Steps { get; } = new();
    public List<string> ExecutionPlan { get; } = new();
    public List<TaskEventRecord> ToolEvents { get; } = new();
    public int TokensConsumed { get; set; } = 0;
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public CancellationTokenSource Cts { get; } = new();
    public ManualResetEventSlim PauseEvent { get; } = new(true);

    public TaskDetailRecord ToDetailRecord()
    {
        TaskStepRecord[] stepsSnapshot;
        string[] planSnapshot;
        lock (Steps)
        {
            stepsSnapshot = Steps.ToArray();
            planSnapshot = ExecutionPlan.ToArray();
        }

        TaskEventRecord[] toolEventsSnapshot;
        lock (ToolEvents)
        {
            toolEventsSnapshot = ToolEvents.ToArray();
        }

        return new TaskDetailRecord(
            Id,
            Prompt,
            SessionId,
            Model,
            Status,
            ProgressPercent,
            stepsSnapshot,
            planSnapshot,
            toolEventsSnapshot,
            TokensConsumed,
            Output,
            CreatedAt,
            StartedAt,
            CompletedAt,
            Error);
    }

    public TaskSummaryRecord ToSummaryRecord() => new(
        Id,
        Prompt,
        SessionId,
        Model,
        Status,
        ProgressPercent,
        TokensConsumed,
        CreatedAt,
        CompletedAt);
}

#endregion

#region Filesystem & Diff DTOs

public sealed record FsTreeNode(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastModified,
    string GitStatus,
    IReadOnlyList<FsTreeNode>? Children = null);

public sealed record FsReadResponse(
    string Path,
    string Content,
    long Size,
    DateTimeOffset LastModified);

public sealed record FsWriteRequest(
    string Path,
    string Content,
    bool Overwrite = true,
    bool CreateDirectory = true);

public sealed record FsWriteResponse(
    bool Success,
    string Path,
    int BytesWritten);

public sealed record FsDiffRequest(
    string Path,
    string ProposedContent);

public sealed record FsDiffResponse(
    string FilePath,
    string OldContent,
    string NewContent,
    int AdditionsCount,
    int DeletionsCount,
    bool HasChanges,
    string UnifiedDiff,
    IReadOnlyList<DiffHunk> Hunks);

public sealed record FsDiffApproveRequest(
    string? DiffId = null,
    string? Path = null,
    string? ProposedContent = null);

public sealed record FsDiffRejectRequest(
    string? DiffId = null,
    string? Path = null);

#endregion

#region Agent DTOs

public sealed record CreateAgentApiRequest(
    string Name,
    string SystemPrompt,
    string? PreferredModel = "gemini-2.5-flash",
    string? WorkingDirectory = "",
    List<string>? ToolPermissions = null,
    int? MaxTokenBudget = 32768,
    string? AutonomyTier = "Guarded");

public sealed record UpdateAgentApiRequest(
    string? Name = null,
    string? SystemPrompt = null,
    string? PreferredModel = null,
    string? WorkingDirectory = null,
    List<string>? ToolPermissions = null,
    int? MaxTokenBudget = null,
    string? AutonomyTier = null);

public sealed record AgentToolPermissionsToggleRequest(
    List<string>? Permissions = null,
    string? Tool = null,
    bool? Enabled = null);

public sealed record AgentDetailResponse(
    AgentConfig Config,
    AgentStatus Status,
    AgentState? State,
    IReadOnlyList<string> EnabledPermissions);

#endregion

#region Settings DTOs

public sealed class DesktopSettings
{
    public bool OnboardingCompleted { get; set; } = false;
    public string PreferredProvider { get; set; } = "gemini";
    public ModelSettings Models { get; set; } = new();
    public ApiKeySettings ApiKeys { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public PermissionSettings Permissions { get; set; } = new();
    public string DefaultWorkingDir { get; set; } = string.Empty;
    public bool TelemetryEnabled { get; set; } = true;

    public DesktopSettings CloneMasked()
    {
        return new DesktopSettings
        {
            OnboardingCompleted = OnboardingCompleted,
            PreferredProvider = PreferredProvider,
            Models = new ModelSettings
            {
                DefaultModel = Models.DefaultModel,
                AvailableModels = new List<string>(Models.AvailableModels),
                FallbackModel = Models.FallbackModel
            },
            ApiKeys = new ApiKeySettings
            {
                GeminiApiKey = MaskKey(ApiKeys.GeminiApiKey),
                OpenAiApiKey = MaskKey(ApiKeys.OpenAiApiKey),
                AnthropicApiKey = MaskKey(ApiKeys.AnthropicApiKey),
                OpenRouterApiKey = MaskKey(ApiKeys.OpenRouterApiKey),
                NousApiKey = MaskKey(ApiKeys.NousApiKey),
                OllamaEndpoint = ApiKeys.OllamaEndpoint,
                EvrenEndpoint = ApiKeys.EvrenEndpoint,
                EvrenApiKey = MaskKey(ApiKeys.EvrenApiKey),
                EvrenCustomHeader = ApiKeys.EvrenCustomHeader,
                EvrenIgnoreSslErrors = ApiKeys.EvrenIgnoreSslErrors,
                CustomEndpoint = ApiKeys.CustomEndpoint,
                CustomApiKey = MaskKey(ApiKeys.CustomApiKey),
                CustomAuthHeader = ApiKeys.CustomAuthHeader,
                CustomIgnoreSslErrors = ApiKeys.CustomIgnoreSslErrors
            },
            Appearance = new AppearanceSettings
            {
                Theme = Appearance.Theme,
                FontSize = Appearance.FontSize,
                FontFamily = Appearance.FontFamily,
                CompactSidebar = Appearance.CompactSidebar
            },
            Permissions = new PermissionSettings
            {
                DefaultAutonomyTier = Permissions.DefaultAutonomyTier,
                AllowedTools = new List<string>(Permissions.AllowedTools),
                RequireApprovalForRed = Permissions.RequireApprovalForRed
            },
            DefaultWorkingDir = DefaultWorkingDir,
            TelemetryEnabled = TelemetryEnabled
        };
    }

    public static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        if (key.Length <= 8) return "***";
        return string.Concat(key.AsSpan(0, 3), "***", key.AsSpan(key.Length - 4));
    }
}

public sealed class ModelSettings
{
    public string DefaultModel { get; set; } = "gemini-2.5-flash";
    public List<string> AvailableModels { get; set; } = new()
    {
        "gemini-2.5-flash",
        "gemini-2.5-pro",
        "claude-3-7-sonnet",
        "gpt-4o",
        "openrouter/auto",
        "nous/hermes-3-llama-3.1-405b",
        "ollama/llama3"
    };
    public string FallbackModel { get; set; } = "offline-mock";
}

public sealed class ApiKeySettings
{
    public string? GeminiApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string? AnthropicApiKey { get; set; }
    public string? OpenRouterApiKey { get; set; }
    public string? NousApiKey { get; set; }
    public string? OllamaEndpoint { get; set; } = "http://localhost:11434";

    // SSB Evren (Kurumsal AI Portal) Settings
    public string? EvrenEndpoint { get; set; } = "https://evren.ssb.gov.tr/v1/chat/completions";
    public string? EvrenApiKey { get; set; }
    public string? EvrenCustomHeader { get; set; }
    public bool EvrenIgnoreSslErrors { get; set; } = false;

    // Universal Custom OpenAI-Compatible Provider Settings
    public string? CustomEndpoint { get; set; }
    public string? CustomApiKey { get; set; }
    public string? CustomAuthHeader { get; set; }
    public bool CustomIgnoreSslErrors { get; set; } = false;
}

public sealed record SetupStatusResponse(
    bool OnboardingCompleted,
    string PreferredProvider,
    string DefaultModel,
    IReadOnlyList<string> ConfiguredProviders,
    DesktopSettings Settings);

public sealed record LlmConnectionTestRequest(
    string Provider,
    string? ApiKey = null,
    string? Model = null,
    string? EndpointUrl = null,
    string? CustomAuthHeader = null,
    bool IgnoreSslErrors = false);

public sealed record LlmConnectionTestResponse(
    bool Success,
    string Provider,
    string Model,
    long LatencyMs,
    string Message);

public sealed record FetchModelsRequest(
    string EndpointUrl,
    string? ApiKey = null,
    string? CustomAuthHeader = null,
    bool IgnoreSslErrors = false);

public sealed record FetchModelsResponse(
    bool Success,
    IReadOnlyList<string> Models,
    string Message);

public sealed record SetupCompleteRequest(
    string PreferredProvider,
    string DefaultModel,
    string? FallbackModel = null,
    string? ApiKey = null,
    string? OllamaEndpoint = null,
    string? DefaultWorkingDir = null,
    string? AutonomyTier = null,
    string? Theme = null,
    string? EndpointUrl = null,
    string? CustomAuthHeader = null,
    bool IgnoreSslErrors = false);

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "dark";
    public int FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "system-ui, -apple-system, sans-serif";
    public bool CompactSidebar { get; set; } = false;
}

public sealed class PermissionSettings
{
    public string DefaultAutonomyTier { get; set; } = "Guarded";
    public List<string> AllowedTools { get; set; } = new()
    {
        "terminal", "filesystem", "git", "cdp", "web", "subagents"
    };
    public bool RequireApprovalForRed { get; set; } = true;
}

#endregion

#region Terminal DTOs

public sealed record TerminalExecRequest(
    string Command,
    string? SessionId = null,
    string? WorkingDir = null,
    int TimeoutSeconds = 30,
    bool Approved = false);

public sealed record TerminalExecResponse(
    int ExitCode,
    string Stdout,
    string Stderr,
    long ExecutionTimeMs,
    string RiskTier,
    bool WasApproved);

#endregion

#region Integrations DTOs

public sealed record IntegrationStatusResponse(
    string Channel,
    bool Configured,
    bool Connected,
    string Mode);

public sealed record IntegrationTestRequest(
    string Channel,
    string? Message = null);

public sealed record IntegrationTestResponse(
    string Channel,
    bool Success,
    string Details);

#endregion

#region WebSocket Protocol Messages

public sealed record WsStreamChunk(
    string TaskId,
    string? SessionId,
    string Chunk,
    bool IsFinal,
    DateTimeOffset Timestamp)
{
    public string Type => "stream_chunk";
}

public sealed record WsToolStarted(
    string TaskId,
    string ToolName,
    string Input,
    DateTimeOffset Timestamp)
{
    public string Type => "tool_started";
}

public sealed record WsToolProgress(
    string TaskId,
    string ToolName,
    int Progress,
    string Message,
    DateTimeOffset Timestamp)
{
    public string Type => "tool_progress";
}

public sealed record WsToolCompleted(
    string TaskId,
    string ToolName,
    string Output,
    long DurationMs,
    string Status,
    DateTimeOffset Timestamp)
{
    public string Type => "tool_completed";
}

public sealed record WsToolFailed(
    string TaskId,
    string ToolName,
    string Error,
    long DurationMs,
    string Status,
    DateTimeOffset Timestamp)
{
    public string Type => "tool_failed";
}

public sealed record WsReasoning(
    string TaskId,
    string Thoughts,
    bool IsFinal,
    DateTimeOffset Timestamp)
{
    public string Type => "reasoning";
}

public sealed record WsTaskProgress(
    string TaskId,
    string Status,
    int ProgressPercent,
    string CurrentStep,
    string Message,
    DateTimeOffset Timestamp)
{
    public string Type => "task_progress";
}

public sealed record WsTokenTelemetry(
    string TaskId,
    string? SessionId,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    double EstimatedCost,
    DateTimeOffset Timestamp)
{
    public string Type => "token_telemetry";
}

public sealed record WsSmartApprovalPrompt(
    string RequestId,
    string ActionName,
    string Tier,
    string Description,
    DateTimeOffset Timestamp)
{
    public string Type => "smart_approval_prompt";
}

public sealed record WsTerminalOutput(
    string Command,
    string Output,
    bool IsError,
    DateTimeOffset Timestamp)
{
    public string Type => "terminal_output";
}

#endregion
