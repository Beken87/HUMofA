namespace HUMofA.Agent;

/// <summary>
/// Bitmask flags representing permissions for tools accessible to an agent.
/// </summary>
[Flags]
public enum AgentToolPermissions
{
    None = 0,
    Terminal = 1 << 0,
    FileOps = 1 << 1,
    WebSearch = 1 << 2,
    BrowserCdp = 1 << 3,
    Git = 1 << 4,
    SubAgents = 1 << 5,
    All = Terminal | FileOps | WebSearch | BrowserCdp | Git | SubAgents
}

/// <summary>
/// Execution status states for an autonomous agent.
/// </summary>
public enum AgentStatus
{
    Idle,
    Running,
    WaitingForApproval,
    Paused,
    Completed,
    Failed
}

/// <summary>
/// Configuration for an agent persona, tool privileges, and execution limits.
/// </summary>
public sealed record AgentConfig(
    string Id,
    string Name,
    string SystemPrompt,
    string PreferredModel = "gemini-2.5-flash",
    string WorkingDirectory = "",
    AgentToolPermissions ToolPermissions = AgentToolPermissions.All,
    int MaxTokenBudget = 32768,
    string AutonomyTier = "Guarded")
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool HasPermission(AgentToolPermissions permission) =>
        (ToolPermissions & permission) == permission;
}

/// <summary>
/// Runtime state tracking an agent's current task and health.
/// </summary>
public sealed record AgentState(
    AgentConfig Config,
    AgentStatus Status,
    string? CurrentTaskId = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? LastActiveAt = null,
    string? LastError = null,
    int TokensConsumed = 0);

/// <summary>
/// Task execution request routed to an agent.
/// </summary>
public sealed record AgentTaskRequest(
    string AgentId,
    string TaskPrompt,
    string? SessionId = null,
    IReadOnlyDictionary<string, string>? AdditionalContext = null);

/// <summary>
/// Execution result from an agent task run.
/// </summary>
public sealed record AgentTaskResult(
    string TaskId,
    string AgentId,
    bool IsSuccess,
    string Output,
    int TokensConsumed,
    AgentStatus FinalStatus,
    IReadOnlyList<string> ContextTranscript);
