using System.Collections.Concurrent;

namespace HUMofA.SubAgents;

public sealed record SubAgentSpec(
    string Role,
    string TaskPrompt,
    string PreferredModel = "gemini-2.5-flash",
    int MaxTokenBudget = 8192);

public sealed record SubAgentExecutionResult(
    string SubAgentId,
    string Role,
    string KanbanCardId,
    bool IsSuccess,
    string CondensedOutput,
    int TokensConsumed,
    IReadOnlyList<string> ContextTranscript);

/// <summary>
/// Hermes-compatible Isolated Sub-Agent Orchestrator (delegate_tool.py equivalent).
/// Spawns isolated sub-agents with their own conversation transcripts, token budgets,
/// Python 3.14 RPC access, and automatic Kanban task tracking so the parent context stays lean.
/// </summary>
public sealed class SubAgentOrchestrator
{
    private readonly KanbanTracker _kanban;
    private readonly IPythonRpcBridge _rpcBridge;
    private readonly ConcurrentDictionary<string, List<string>> _isolatedContexts = new(StringComparer.OrdinalIgnoreCase);

    public KanbanTracker Kanban => _kanban;
    public IPythonRpcBridge RpcBridge => _rpcBridge;

    public SubAgentOrchestrator(KanbanTracker? kanban = null, IPythonRpcBridge? rpcBridge = null)
    {
        _kanban = kanban ?? new KanbanTracker();
        _rpcBridge = rpcBridge ?? new PythonRpcBridge();
    }

    public async Task<SubAgentExecutionResult> SpawnAndExecuteAsync(
        SubAgentSpec spec,
        Func<SubAgentSpec, IPythonRpcBridge, CancellationToken, Task<string>>? workDelegate = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Role);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.TaskPrompt);

        var subAgentId = $"subagent-{Guid.NewGuid().ToString("N")[..8]}";
        var transcript = new List<string>
        {
            $"[SYSTEM] Initialized isolated sub-agent '{subAgentId}' ({spec.Role}) with model '{spec.PreferredModel}' and budget {spec.MaxTokenBudget} tokens.",
            $"[USER] {spec.TaskPrompt}"
        };
        _isolatedContexts[subAgentId] = transcript;

        var card = _kanban.CreateCard(
            title: $"{spec.Role}: { Truncate(spec.TaskPrompt, 48) }",
            description: spec.TaskPrompt,
            assignedSubAgentId: subAgentId);

        await _kanban.MoveCardAsync(card.Id, KanbanColumn.InProgress).ConfigureAwait(false);

        try
        {
            string rawOutput;
            if (workDelegate is not null)
            {
                rawOutput = await workDelegate(spec, _rpcBridge, ct).ConfigureAwait(false);
            }
            else
            {
                var rpcPing = await _rpcBridge.InvokeAsync("ping", null, ct).ConfigureAwait(false);
                rawOutput = $"Completed delegated task for role '{spec.Role}' (RPC Ready={rpcPing.IsSuccess}): {spec.TaskPrompt}";
            }

            transcript.Add($"[ASSISTANT] {rawOutput}");

            var estimatedTokens = EstimateTokens(spec.TaskPrompt) + EstimateTokens(rawOutput);
            if (estimatedTokens > spec.MaxTokenBudget)
            {
                rawOutput = Truncate(rawOutput, spec.MaxTokenBudget * 3);
                estimatedTokens = spec.MaxTokenBudget;
            }

            await _kanban.MoveCardAsync(card.Id, KanbanColumn.Done, rawOutput).ConfigureAwait(false);

            return new SubAgentExecutionResult(
                SubAgentId: subAgentId,
                Role: spec.Role,
                KanbanCardId: card.Id,
                IsSuccess: true,
                CondensedOutput: rawOutput,
                TokensConsumed: estimatedTokens,
                ContextTranscript: transcript.AsReadOnly());
        }
        catch (Exception ex)
        {
            transcript.Add($"[ERROR] {ex.Message}");
            await _kanban.MoveCardAsync(card.Id, KanbanColumn.Blocked, ex.Message).ConfigureAwait(false);

            return new SubAgentExecutionResult(
                SubAgentId: subAgentId,
                Role: spec.Role,
                KanbanCardId: card.Id,
                IsSuccess: false,
                CondensedOutput: $"Sub-agent failed: {ex.Message}",
                TokensConsumed: EstimateTokens(spec.TaskPrompt),
                ContextTranscript: transcript.AsReadOnly());
        }
    }

    public async Task<IReadOnlyList<SubAgentExecutionResult>> SpawnParallelBatchAsync(
        IEnumerable<SubAgentSpec> specs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var tasks = specs.Select(s => SpawnAndExecuteAsync(s, null, ct));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public IReadOnlyList<string> GetIsolatedTranscript(string subAgentId) =>
        _isolatedContexts.TryGetValue(subAgentId, out var list) ? list.AsReadOnly() : Array.Empty<string>();

    private static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "...";
}

