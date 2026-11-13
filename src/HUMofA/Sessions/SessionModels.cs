namespace HUMofA.Sessions;

/// <summary>
/// Autonomous execution autonomy tiers.
/// </summary>
public static class AutonomyTiers
{
    public const string Guarded = "Guarded";
    public const string FullAutonomy = "FullAutonomy";
    public const string Conservative = "Conservative";
}

/// <summary>
/// Persistent session record.
/// </summary>
public sealed record SessionRecord(
    string Id,
    string Title,
    string Model,
    string WorkingDir,
    string AutonomyTier,
    int MaxTokens,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsActive = false,
    bool Pinned = false,
    int MessageCount = 0,
    int TotalTokens = 0);

/// <summary>
/// Chat message within a persistent session transcript.
/// </summary>
public sealed record SessionMessage(
    string Id,
    string SessionId,
    string Role,
    string Content,
    DateTimeOffset Timestamp,
    int TokenCount = 0,
    string? ToolCallsJson = null);

/// <summary>
/// Tool execution record tracked in the session database.
/// </summary>
public sealed record ToolCallRecord(
    string Id,
    string SessionId,
    string ToolName,
    string CallArgs,
    string Result,
    long ExecutionTimeMs,
    string Status,
    DateTimeOffset Timestamp);

/// <summary>
/// Session-scoped permission grant for autonomous actions.
/// </summary>
public sealed record SessionPermissionGrant(
    string Id,
    string SessionId,
    string ToolName,
    string ActionPattern,
    string RiskTier,
    DateTimeOffset GrantedAt);
