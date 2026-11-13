using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HUMofA.Sessions;

/// <summary>
/// SQLite WAL-backed persistent session engine.
/// Stores sessions, message transcripts, tool executions, and session permission grants.
/// Supports both file-based storage and in-memory (:memory:) execution for fast unit tests.
/// </summary>
public sealed class SessionManager : ISessionManager
{
    public const string DefaultDatabasePath = ".humofa-data/sessions/sessions.db";

    private readonly string _connectionString;
    private readonly SqliteConnection _keepAliveConnection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public SessionManager(string? databasePath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || databasePath == ":memory:")
        {
            var dbName = $"humofa_sess_{Guid.NewGuid():N}";
            _connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        }
        else
        {
            var fullPath = Path.GetFullPath(databasePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();
        }

        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _keepAliveConnection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                model TEXT NOT NULL,
                working_dir TEXT NOT NULL,
                autonomy_tier TEXT NOT NULL,
                max_tokens INTEGER NOT NULL DEFAULT 8192,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 0,
                pinned INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                token_count INTEGER NOT NULL DEFAULT 0,
                tool_calls_json TEXT,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS tool_calls (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                call_args TEXT NOT NULL,
                result TEXT NOT NULL,
                execution_time_ms INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS session_permissions (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                action_pattern TEXT NOT NULL,
                risk_tier TEXT NOT NULL,
                granted_at TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id, timestamp ASC);
            CREATE INDEX IF NOT EXISTS idx_tool_calls_session ON tool_calls(session_id, timestamp ASC);
            CREATE INDEX IF NOT EXISTS idx_session_permissions ON session_permissions(session_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public async Task<SessionRecord> CreateSessionAsync(
        string title,
        string model = "gemini-2.5-flash",
        string? workingDir = null,
        string autonomyTier = AutonomyTiers.Guarded,
        int maxTokens = 8192,
        bool pinned = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var id = $"sess_{Guid.NewGuid():N}"[..17];
        var now = DateTimeOffset.UtcNow;
        var dir = string.IsNullOrWhiteSpace(workingDir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(workingDir);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            // If this is the first session, make it active
            using var countCmd = conn.CreateCommand();
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE is_active = 1;";
            var activeCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            var makeActive = activeCount == 0;

            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO sessions (id, title, model, working_dir, autonomy_tier, max_tokens, created_at, updated_at, is_active, pinned)
                VALUES (@id, @title, @model, @working_dir, @autonomy_tier, @max_tokens, @created_at, @updated_at, @is_active, @pinned);
                """;
            insertCmd.Parameters.AddWithValue("@id", id);
            insertCmd.Parameters.AddWithValue("@title", title.Trim());
            insertCmd.Parameters.AddWithValue("@model", model);
            insertCmd.Parameters.AddWithValue("@working_dir", dir);
            insertCmd.Parameters.AddWithValue("@autonomy_tier", autonomyTier);
            insertCmd.Parameters.AddWithValue("@max_tokens", maxTokens);
            insertCmd.Parameters.AddWithValue("@created_at", now.ToString("O"));
            insertCmd.Parameters.AddWithValue("@updated_at", now.ToString("O"));
            insertCmd.Parameters.AddWithValue("@is_active", makeActive ? 1 : 0);
            insertCmd.Parameters.AddWithValue("@pinned", pinned ? 1 : 0);

            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            tx.Commit();

            return new SessionRecord(
                Id: id,
                Title: title.Trim(),
                Model: model,
                WorkingDir: dir,
                AutonomyTier: autonomyTier,
                MaxTokens: maxTokens,
                CreatedAt: now,
                UpdatedAt: now,
                IsActive: makeActive,
                Pinned: pinned,
                MessageCount: 0,
                TotalTokens: 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SessionRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.id, s.title, s.model, s.working_dir, s.autonomy_tier, s.max_tokens,
                       s.created_at, s.updated_at, s.is_active, s.pinned,
                       (SELECT COUNT(*) FROM messages WHERE session_id = s.id) AS msg_count,
                       (SELECT COALESCE(SUM(token_count), 0) FROM messages WHERE session_id = s.id) AS token_sum
                FROM sessions s
                WHERE s.id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", sessionId);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return MapSession(reader);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.id, s.title, s.model, s.working_dir, s.autonomy_tier, s.max_tokens,
                       s.created_at, s.updated_at, s.is_active, s.pinned,
                       (SELECT COUNT(*) FROM messages WHERE session_id = s.id) AS msg_count,
                       (SELECT COALESCE(SUM(token_count), 0) FROM messages WHERE session_id = s.id) AS token_sum
                FROM sessions s
                ORDER BY s.pinned DESC, s.updated_at DESC;
                """;

            var results = new List<SessionRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(MapSession(reader));
            }
            return results.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SessionRecord?> GetActiveSessionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.id, s.title, s.model, s.working_dir, s.autonomy_tier, s.max_tokens,
                       s.created_at, s.updated_at, s.is_active, s.pinned,
                       (SELECT COUNT(*) FROM messages WHERE session_id = s.id) AS msg_count,
                       (SELECT COALESCE(SUM(token_count), 0) FROM messages WHERE session_id = s.id) AS token_sum
                FROM sessions s
                WHERE s.is_active = 1
                LIMIT 1;
                """;

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return MapSession(reader);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetActiveSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            // Verify session exists
            using var checkCmd = conn.CreateCommand();
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = @id;";
            checkCmd.Parameters.AddWithValue("@id", sessionId);
            var exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
            if (!exists)
            {
                return false;
            }

            using var resetCmd = conn.CreateCommand();
            resetCmd.Transaction = tx;
            resetCmd.CommandText = "UPDATE sessions SET is_active = 0;";
            await resetCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var setCmd = conn.CreateCommand();
            setCmd.Transaction = tx;
            setCmd.CommandText = "UPDATE sessions SET is_active = 1, updated_at = @now WHERE id = @id;";
            setCmd.Parameters.AddWithValue("@id", sessionId);
            setCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> UpdateSessionAsync(
        string sessionId,
        string? title = null,
        string? model = null,
        string? workingDir = null,
        string? autonomyTier = null,
        int? maxTokens = null,
        bool? pinned = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var clauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (title is not null)
        {
            clauses.Add("title = @title");
            parameters.Add(new SqliteParameter("@title", title.Trim()));
        }
        if (model is not null)
        {
            clauses.Add("model = @model");
            parameters.Add(new SqliteParameter("@model", model));
        }
        if (workingDir is not null)
        {
            clauses.Add("working_dir = @working_dir");
            parameters.Add(new SqliteParameter("@working_dir", Path.GetFullPath(workingDir)));
        }
        if (autonomyTier is not null)
        {
            clauses.Add("autonomy_tier = @autonomy_tier");
            parameters.Add(new SqliteParameter("@autonomy_tier", autonomyTier));
        }
        if (maxTokens.HasValue)
        {
            clauses.Add("max_tokens = @max_tokens");
            parameters.Add(new SqliteParameter("@max_tokens", maxTokens.Value));
        }
        if (pinned.HasValue)
        {
            clauses.Add("pinned = @pinned");
            parameters.Add(new SqliteParameter("@pinned", pinned.Value ? 1 : 0));
        }

        clauses.Add("updated_at = @updated_at");
        parameters.Add(new SqliteParameter("@updated_at", DateTimeOffset.UtcNow.ToString("O")));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE sessions SET {string.Join(", ", clauses)} WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", sessionId);
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p);
            }

            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return rows > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", sessionId);

            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return rows > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SessionMessage> AppendMessageAsync(
        string sessionId,
        string role,
        string content,
        int tokenCount = 0,
        string? toolCallsJson = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(content);

        var id = $"msg_{Guid.NewGuid():N}"[..16];
        var now = DateTimeOffset.UtcNow;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO messages (id, session_id, role, content, timestamp, token_count, tool_calls_json)
                VALUES (@id, @session_id, @role, @content, @timestamp, @token_count, @tool_calls_json);
                """;
            insertCmd.Parameters.AddWithValue("@id", id);
            insertCmd.Parameters.AddWithValue("@session_id", sessionId);
            insertCmd.Parameters.AddWithValue("@role", role.ToLowerInvariant());
            insertCmd.Parameters.AddWithValue("@content", content);
            insertCmd.Parameters.AddWithValue("@timestamp", now.ToString("O"));
            insertCmd.Parameters.AddWithValue("@token_count", tokenCount);
            insertCmd.Parameters.AddWithValue("@tool_calls_json", (object?)toolCallsJson ?? DBNull.Value);

            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = "UPDATE sessions SET updated_at = @now WHERE id = @session_id;";
            updateCmd.Parameters.AddWithValue("@now", now.ToString("O"));
            updateCmd.Parameters.AddWithValue("@session_id", sessionId);
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();

            return new SessionMessage(
                Id: id,
                SessionId: sessionId,
                Role: role.ToLowerInvariant(),
                Content: content,
                Timestamp: now,
                TokenCount: tokenCount,
                ToolCallsJson: toolCallsJson);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SessionMessage>> GetTranscriptAsync(
        string sessionId,
        int limit = 1000,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, role, content, timestamp, token_count, tool_calls_json
                FROM messages
                WHERE session_id = @session_id
                ORDER BY timestamp ASC, rowid ASC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@session_id", sessionId);
            cmd.Parameters.AddWithValue("@limit", limit);

            var messages = new List<SessionMessage>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                messages.Add(new SessionMessage(
                    Id: reader.GetString(0),
                    SessionId: reader.GetString(1),
                    Role: reader.GetString(2),
                    Content: reader.GetString(3),
                    Timestamp: DateTimeOffset.Parse(reader.GetString(4)),
                    TokenCount: reader.GetInt32(5),
                    ToolCallsJson: reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
            return messages.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ToolCallRecord> RecordToolCallAsync(
        string sessionId,
        string toolName,
        string callArgs,
        string result,
        long executionTimeMs,
        string status,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var id = $"tc_{Guid.NewGuid():N}"[..16];
        var now = DateTimeOffset.UtcNow;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO tool_calls (id, session_id, tool_name, call_args, result, execution_time_ms, status, timestamp)
                VALUES (@id, @session_id, @tool_name, @call_args, @result, @execution_time_ms, @status, @timestamp);
                """;
            insertCmd.Parameters.AddWithValue("@id", id);
            insertCmd.Parameters.AddWithValue("@session_id", sessionId);
            insertCmd.Parameters.AddWithValue("@tool_name", toolName);
            insertCmd.Parameters.AddWithValue("@call_args", callArgs ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@result", result ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@execution_time_ms", executionTimeMs);
            insertCmd.Parameters.AddWithValue("@status", status ?? "Success");
            insertCmd.Parameters.AddWithValue("@timestamp", now.ToString("O"));

            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = "UPDATE sessions SET updated_at = @now WHERE id = @session_id;";
            updateCmd.Parameters.AddWithValue("@now", now.ToString("O"));
            updateCmd.Parameters.AddWithValue("@session_id", sessionId);
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();

            return new ToolCallRecord(
                Id: id,
                SessionId: sessionId,
                ToolName: toolName,
                CallArgs: callArgs ?? string.Empty,
                Result: result ?? string.Empty,
                ExecutionTimeMs: executionTimeMs,
                Status: status ?? "Success",
                Timestamp: now);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ToolCallRecord>> GetToolCallsAsync(
        string sessionId,
        int limit = 500,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, tool_name, call_args, result, execution_time_ms, status, timestamp
                FROM tool_calls
                WHERE session_id = @session_id
                ORDER BY timestamp ASC, rowid ASC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@session_id", sessionId);
            cmd.Parameters.AddWithValue("@limit", limit);

            var calls = new List<ToolCallRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                calls.Add(new ToolCallRecord(
                    Id: reader.GetString(0),
                    SessionId: reader.GetString(1),
                    ToolName: reader.GetString(2),
                    CallArgs: reader.GetString(3),
                    Result: reader.GetString(4),
                    ExecutionTimeMs: reader.GetInt64(5),
                    Status: reader.GetString(6),
                    Timestamp: DateTimeOffset.Parse(reader.GetString(7))));
            }
            return calls.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SessionPermissionGrant> AddPermissionGrantAsync(
        string sessionId,
        string toolName,
        string actionPattern,
        string riskTier,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionPattern);

        var id = $"perm_{Guid.NewGuid():N}"[..16];
        var now = DateTimeOffset.UtcNow;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO session_permissions (id, session_id, tool_name, action_pattern, risk_tier, granted_at)
                VALUES (@id, @session_id, @tool_name, @action_pattern, @risk_tier, @granted_at);
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@session_id", sessionId);
            cmd.Parameters.AddWithValue("@tool_name", toolName);
            cmd.Parameters.AddWithValue("@action_pattern", actionPattern);
            cmd.Parameters.AddWithValue("@risk_tier", riskTier);
            cmd.Parameters.AddWithValue("@granted_at", now.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return new SessionPermissionGrant(
                Id: id,
                SessionId: sessionId,
                ToolName: toolName,
                ActionPattern: actionPattern,
                RiskTier: riskTier,
                GrantedAt: now);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> CheckPermissionGrantAsync(
        string sessionId,
        string toolName,
        string actionPattern,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var grants = await GetPermissionGrantsAsync(sessionId, ct).ConfigureAwait(false);
        foreach (var grant in grants)
        {
            var toolMatch = grant.ToolName == "*" ||
                            string.Equals(grant.ToolName, toolName, StringComparison.OrdinalIgnoreCase);

            if (toolMatch && MatchesActionPattern(grant.ActionPattern, actionPattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesActionPattern(string pattern, string action)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            return true;

        if (string.Equals(pattern, action, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var regex = "^" + Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";
            return Regex.IsMatch(action, regex, RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<SessionPermissionGrant>> GetPermissionGrantsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, tool_name, action_pattern, risk_tier, granted_at
                FROM session_permissions
                WHERE session_id = @session_id
                ORDER BY granted_at ASC;
                """;
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            var grants = new List<SessionPermissionGrant>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                grants.Add(new SessionPermissionGrant(
                    Id: reader.GetString(0),
                    SessionId: reader.GetString(1),
                    ToolName: reader.GetString(2),
                    ActionPattern: reader.GetString(3),
                    RiskTier: reader.GetString(4),
                    GrantedAt: DateTimeOffset.Parse(reader.GetString(5))));
            }
            return grants.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RevokePermissionGrantAsync(string grantId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM session_permissions WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", grantId);

            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return rows > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> ClearPermissionGrantsAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM session_permissions WHERE session_id = @session_id;";
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static SessionRecord MapSession(SqliteDataReader reader)
    {
        return new SessionRecord(
            Id: reader.GetString(0),
            Title: reader.GetString(1),
            Model: reader.GetString(2),
            WorkingDir: reader.GetString(3),
            AutonomyTier: reader.GetString(4),
            MaxTokens: reader.GetInt32(5),
            CreatedAt: DateTimeOffset.Parse(reader.GetString(6)),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(7)),
            IsActive: reader.GetInt32(8) == 1,
            Pinned: reader.GetInt32(9) == 1,
            MessageCount: reader.GetInt32(10),
            TotalTokens: Convert.ToInt32(reader.GetInt64(11)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keepAliveConnection.Dispose();
        _lock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _keepAliveConnection.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }
}
