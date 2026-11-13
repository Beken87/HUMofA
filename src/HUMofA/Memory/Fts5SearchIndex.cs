using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HUMofA.Memory;

public sealed record MemorySearchHit(
    string Id,
    string Category,
    string Title,
    string Content,
    string Source,
    double Bm25Score,
    DateTimeOffset UpdatedAt);

/// <summary>
/// SQLite FTS5 full-text search index with BM25 ranking for memories, skills, and session transcripts.
/// </summary>
public sealed class Fts5SearchIndex : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _keepAliveConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public Fts5SearchIndex(string? databasePath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || databasePath == ":memory:")
        {
            var dbName = $"humofa_fts_{Guid.NewGuid():N}";
            _connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
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
            CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
                id UNINDEXED,
                category,
                title,
                content,
                source,
                updated_at UNINDEXED,
                tokenize = 'porter unicode61'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task UpsertDocumentAsync(
        string id,
        string category,
        string title,
        string content,
        string source = "local",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        title ??= string.Empty;
        content ??= string.Empty;
        source ??= "local";

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = (SqliteTransaction)tx;
                delCmd.CommandText = "DELETE FROM memory_fts WHERE id = $id;";
                delCmd.Parameters.AddWithValue("$id", id);
                await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var insCmd = conn.CreateCommand())
            {
                insCmd.Transaction = (SqliteTransaction)tx;
                insCmd.CommandText = """
                    INSERT INTO memory_fts (id, category, title, content, source, updated_at)
                    VALUES ($id, $category, $title, $content, $source, $updatedAt);
                    """;
                insCmd.Parameters.AddWithValue("$id", id);
                insCmd.Parameters.AddWithValue("$category", category);
                insCmd.Parameters.AddWithValue("$title", title);
                insCmd.Parameters.AddWithValue("$content", content);
                insCmd.Parameters.AddWithValue("$source", source);
                insCmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                await insCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteDocumentAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM memory_fts WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return affected > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        string query,
        string? categoryFilter = null,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return Array.Empty<MemorySearchHit>();

        var sanitizedQuery = SanitizeFts5Query(query);
        if (string.IsNullOrWhiteSpace(sanitizedQuery))
            return Array.Empty<MemorySearchHit>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(categoryFilter))
        {
            cmd.CommandText = """
                SELECT id, category, title, content, source, bm25(memory_fts) AS score, updated_at
                FROM memory_fts
                WHERE memory_fts MATCH $query AND category = $category
                ORDER BY score ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$category", categoryFilter);
        }
        else
        {
            cmd.CommandText = """
                SELECT id, category, title, content, source, bm25(memory_fts) AS score, updated_at
                FROM memory_fts
                WHERE memory_fts MATCH $query
                ORDER BY score ASC
                LIMIT $limit;
                """;
        }

        cmd.Parameters.AddWithValue("$query", sanitizedQuery);
        cmd.Parameters.AddWithValue("$limit", limit);

        var hits = new List<MemorySearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var rawScore = reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5);
            var updatedStr = reader.IsDBNull(6) ? null : reader.GetString(6);
            if (!DateTimeOffset.TryParse(updatedStr, out var updatedAt))
                updatedAt = DateTimeOffset.UtcNow;

            // In SQLite FTS5, bm25() returns negative numbers where more negative = better match.
            // Convert to positive relevance score for intuitive consumption.
            var relevance = Math.Abs(rawScore);

            hits.Add(new MemorySearchHit(
                Id: reader.GetString(0),
                Category: reader.GetString(1),
                Title: reader.GetString(2),
                Content: reader.GetString(3),
                Source: reader.GetString(4),
                Bm25Score: relevance,
                UpdatedAt: updatedAt));
        }

        return hits;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_fts;";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    public static string SanitizeFts5Query(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return string.Empty;

        // Extract word tokens (letters, digits, underscores) so FTS5 syntax characters never throw
        var matches = Regex.Matches(rawQuery, @"[\p{L}\p{N}_]+");
        if (matches.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (int i = 0; i < matches.Count; i++)
        {
            if (i > 0)
                sb.Append(" OR ");
            sb.Append('"').Append(matches[i].Value).Append("\"*");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _keepAliveConnection.Dispose();
        _writeLock.Dispose();
    }
}

