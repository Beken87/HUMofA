using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace HUMofA.SubAgents;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KanbanColumn
{
    Pending,
    InProgress,
    Blocked,
    Done
}

public sealed record KanbanTaskCard(
    string Id,
    string Title,
    string Description,
    KanbanColumn Column,
    string? AssignedSubAgentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ResultSummary = null);

public sealed record KanbanTransitionEvent(
    KanbanTaskCard Card,
    KanbanColumn FromColumn,
    KanbanColumn ToColumn,
    DateTimeOffset Timestamp);

/// <summary>
/// Hermes-compatible Kanban Task Board and Watcher Dispatcher (kanban_tools.py & kanban_watchers.py equivalent).
/// Tracks sub-agent task states and fires registered watcher callbacks on column transitions.
/// </summary>
public sealed class KanbanTracker
{
    private readonly ConcurrentDictionary<string, KanbanTaskCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Func<KanbanTransitionEvent, Task>> _watchers = new();
    private readonly object _watcherLock = new();

    public IReadOnlyCollection<KanbanTaskCard> AllCards => _cards.Values.ToList();

    public KanbanTaskCard CreateCard(string title, string description, string? assignedSubAgentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        description ??= string.Empty;

        var now = DateTimeOffset.UtcNow;
        var id = $"task-{Guid.NewGuid().ToString("N")[..8]}";
        var card = new KanbanTaskCard(
            Id: id,
            Title: title.Trim(),
            Description: description.Trim(),
            Column: KanbanColumn.Pending,
            AssignedSubAgentId: assignedSubAgentId,
            CreatedAt: now,
            UpdatedAt: now);

        _cards[id] = card;
        return card;
    }

    public void RegisterWatcher(Func<KanbanTransitionEvent, Task> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_watcherLock)
        {
            _watchers.Add(watcher);
        }
    }

    public async Task<KanbanTaskCard> MoveCardAsync(
        string cardId,
        KanbanColumn targetColumn,
        string? resultSummary = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        if (!_cards.TryGetValue(cardId, out var existing))
            throw new KeyNotFoundException($"Kanban card '{cardId}' not found.");

        var previousColumn = existing.Column;
        var updated = existing with
        {
            Column = targetColumn,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResultSummary = resultSummary ?? existing.ResultSummary
        };

        _cards[cardId] = updated;

        if (previousColumn != targetColumn)
        {
            var evt = new KanbanTransitionEvent(updated, previousColumn, targetColumn, DateTimeOffset.UtcNow);
            List<Func<KanbanTransitionEvent, Task>> snapshot;
            lock (_watcherLock)
            {
                snapshot = _watchers.ToList();
            }

            foreach (var watcher in snapshot)
            {
                await watcher(evt).ConfigureAwait(false);
            }
        }

        return updated;
    }

    public IReadOnlyList<KanbanTaskCard> GetCardsByColumn(KanbanColumn column) =>
        _cards.Values.Where(c => c.Column == column).OrderBy(c => c.CreatedAt).ToList();

    public KanbanTaskCard? GetCard(string cardId) =>
        _cards.TryGetValue(cardId, out var card) ? card : null;
}

