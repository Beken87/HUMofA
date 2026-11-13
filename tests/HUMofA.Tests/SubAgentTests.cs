using FluentAssertions;
using HUMofA.SubAgents;
using Xunit;

namespace HUMofA.Tests;

public class SubAgentTests
{
    [Fact]
    public async Task PythonRpcBridge_ExecutesJsonRpcMethodsOverStdio()
    {
        await using var bridge = new PythonRpcBridge();
        await bridge.StartAsync();
        bridge.IsRunning.Should().BeTrue();

        var pingRes = await bridge.InvokeAsync("ping");
        pingRes.IsSuccess.Should().BeTrue();
        pingRes.Result.Should().NotBeNull();
        pingRes.Result!.Value.GetProperty("pong").GetBoolean().Should().BeTrue();

        var addRes = await bridge.InvokeAsync("add", new { a = 19, b = 23 });
        addRes.IsSuccess.Should().BeTrue();
        addRes.Result!.Value.GetDouble().Should().Be(42);

        var evalRes = await bridge.InvokeAsync("eval_expr", new { expression = "2 ** 10" });
        evalRes.IsSuccess.Should().BeTrue();
        evalRes.Result!.Value.GetInt32().Should().Be(1024);
    }

    [Fact]
    public async Task KanbanTracker_ManagesLifecycle_AndFiresTransitionWatchers()
    {
        var tracker = new KanbanTracker();
        var observedTransitions = new List<(KanbanColumn From, KanbanColumn To)>();

        tracker.RegisterWatcher(evt =>
        {
            observedTransitions.Add((evt.FromColumn, evt.ToColumn));
            return Task.CompletedTask;
        });

        var card = tracker.CreateCard("Implement FTS5 Search", "Build SQLite BM25 index", "subagent-1");
        card.Column.Should().Be(KanbanColumn.Pending);

        await tracker.MoveCardAsync(card.Id, KanbanColumn.InProgress);
        await tracker.MoveCardAsync(card.Id, KanbanColumn.Done, "Completed successfully");

        var updated = tracker.GetCard(card.Id);
        updated.Should().NotBeNull();
        updated!.Column.Should().Be(KanbanColumn.Done);

        observedTransitions.Should().Equal([
            (KanbanColumn.Pending, KanbanColumn.InProgress),
            (KanbanColumn.InProgress, KanbanColumn.Done)
        ]);
    }

    [Fact]
    public async Task SubAgentOrchestrator_RunsIsolatedSubAgents_AndEnforcesTokenBudget()
    {
        var orchestrator = new SubAgentOrchestrator();

        var spec = new SubAgentSpec(
            Role: "Math Analyst",
            TaskPrompt: "Compute statistics via Python RPC",
            PreferredModel: "gemini-2.5-flash",
            MaxTokenBudget: 2048);

        var result = await orchestrator.SpawnAndExecuteAsync(spec, async (s, rpc, ct) =>
        {
            var resp = await rpc.InvokeAsync("eval_expr", new { expression = "sum([10, 20, 30, 40])" }, ct);
            return $"PythonRPC={resp.Result!.Value.GetInt32()}";
        });

        result.IsSuccess.Should().BeTrue();
        result.CondensedOutput.Should().Contain("PythonRPC=100");
        result.TokensConsumed.Should().BeLessThanOrEqualTo(2048);

        var card = orchestrator.Kanban.GetCard(result.KanbanCardId);
        card.Should().NotBeNull();
        card!.Column.Should().Be(KanbanColumn.Done);

        // Verify parallel batch execution
        var batch = await orchestrator.SpawnParallelBatchAsync([
            new SubAgentSpec("Researcher 1", "Analyze module A"),
            new SubAgentSpec("Researcher 2", "Analyze module B")
        ]);
        batch.Should().HaveCount(2);
        batch.Should().OnlyContain(r => r.IsSuccess);

        await orchestrator.RpcBridge.DisposeAsync();
    }
}
