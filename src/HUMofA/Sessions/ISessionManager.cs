namespace HUMofA.Sessions;

/// <summary>
/// Contract for managing persistent agent sessions, transcripts, tool calls, and permission grants.
/// </summary>
public interface ISessionManager : IDisposable, IAsyncDisposable
{
    Task<SessionRecord> CreateSessionAsync(
        string title,
        string model = "gemini-2.5-flash",
        string? workingDir = null,
        string autonomyTier = AutonomyTiers.Guarded,
        int maxTokens = 8192,
        bool pinned = false,
        CancellationToken ct = default);

    Task<SessionRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken ct = default);

    Task<SessionRecord?> GetActiveSessionAsync(CancellationToken ct = default);

    Task<bool> SetActiveSessionAsync(string sessionId, CancellationToken ct = default);

    Task<bool> UpdateSessionAsync(
        string sessionId,
        string? title = null,
        string? model = null,
        string? workingDir = null,
        string? autonomyTier = null,
        int? maxTokens = null,
        bool? pinned = null,
        CancellationToken ct = default);

    Task<bool> DeleteSessionAsync(string sessionId, CancellationToken ct = default);

    Task<SessionMessage> AppendMessageAsync(
        string sessionId,
        string role,
        string content,
        int tokenCount = 0,
        string? toolCallsJson = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<SessionMessage>> GetTranscriptAsync(
        string sessionId,
        int limit = 1000,
        CancellationToken ct = default);

    Task<ToolCallRecord> RecordToolCallAsync(
        string sessionId,
        string toolName,
        string callArgs,
        string result,
        long executionTimeMs,
        string status,
        CancellationToken ct = default);

    Task<IReadOnlyList<ToolCallRecord>> GetToolCallsAsync(
        string sessionId,
        int limit = 500,
        CancellationToken ct = default);

    Task<SessionPermissionGrant> AddPermissionGrantAsync(
        string sessionId,
        string toolName,
        string actionPattern,
        string riskTier,
        CancellationToken ct = default);

    Task<bool> CheckPermissionGrantAsync(
        string sessionId,
        string toolName,
        string actionPattern,
        CancellationToken ct = default);

    Task<IReadOnlyList<SessionPermissionGrant>> GetPermissionGrantsAsync(
        string sessionId,
        CancellationToken ct = default);

    Task<bool> RevokePermissionGrantAsync(string grantId, CancellationToken ct = default);

    Task<int> ClearPermissionGrantsAsync(string sessionId, CancellationToken ct = default);
}
