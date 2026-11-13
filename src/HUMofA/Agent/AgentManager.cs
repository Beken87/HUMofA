using System.Collections.Concurrent;
using HUMofA.SubAgents;

namespace HUMofA.Agent;

/// <summary>
/// Thread-safe Agent Manager coordinating agent personas, tool permissions,
/// lifecycle status transitions, and isolated sub-agent task delegation.
/// </summary>
public sealed class AgentManager : IAgentManager
{
    private readonly SubAgentOrchestrator _orchestrator;
    private readonly ConcurrentDictionary<string, AgentState> _agents = new(StringComparer.OrdinalIgnoreCase);

    public SubAgentOrchestrator Orchestrator => _orchestrator;

    public AgentManager(SubAgentOrchestrator? orchestrator = null)
    {
        _orchestrator = orchestrator ?? new SubAgentOrchestrator();
        RegisterDefaultAgents();
    }

    private void RegisterDefaultAgents()
    {
        var primary = new AgentConfig(
            Id: "default-agent",
            Name: "HUMofA Primary Agent",
            SystemPrompt: "You are HUMofA, an autonomous AI assistant combining Hermes capabilities, Claude Code CLI, and desktop automation.",
            PreferredModel: "gemini-2.5-flash",
            ToolPermissions: AgentToolPermissions.All,
            MaxTokenBudget: 32768,
            AutonomyTier: "Guarded");

        var reviewer = new AgentConfig(
            Id: "code-reviewer",
            Name: "Code Reviewer",
            SystemPrompt: "You are an expert static analysis and code review agent.",
            PreferredModel: "gemini-2.5-flash",
            ToolPermissions: AgentToolPermissions.Terminal | AgentToolPermissions.FileOps | AgentToolPermissions.Git | AgentToolPermissions.SubAgents,
            MaxTokenBudget: 16384,
            AutonomyTier: "Guarded");

        var bugFixer = new AgentConfig(
            Id: "bug-fixer",
            Name: "Bug Fixer",
            SystemPrompt: "You are a specialized debugging and defect remediation agent.",
            PreferredModel: "gemini-2.5-flash",
            ToolPermissions: AgentToolPermissions.Terminal | AgentToolPermissions.FileOps | AgentToolPermissions.Git,
            MaxTokenBudget: 16384,
            AutonomyTier: "Guarded");

        RegisterAgent(primary);
        RegisterAgent(reviewer);
        RegisterAgent(bugFixer);
    }

    public AgentConfig RegisterAgent(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Name);

        var state = new AgentState(
            Config: config,
            Status: AgentStatus.Idle,
            CurrentTaskId: null,
            StartedAt: null,
            LastActiveAt: DateTimeOffset.UtcNow,
            LastError: null,
            TokensConsumed: 0);

        _agents[config.Id] = state;
        return config;
    }

    public AgentConfig? GetAgent(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _agents.TryGetValue(id, out var state) ? state.Config : null;
    }

    public IReadOnlyList<AgentConfig> ListAgents()
    {
        return _agents.Values.Select(s => s.Config).ToList().AsReadOnly();
    }

    public bool UpdateAgentConfig(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Id);

        if (!_agents.TryGetValue(config.Id, out var existingState))
        {
            return false;
        }

        var updatedConfig = config with { UpdatedAt = DateTimeOffset.UtcNow };
        var newState = existingState with
        {
            Config = updatedConfig,
            LastActiveAt = DateTimeOffset.UtcNow
        };

        return _agents.TryUpdate(config.Id, newState, existingState);
    }

    public bool DeleteAgent(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _agents.TryRemove(id, out _);
    }

    public AgentStatus GetAgentStatus(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (_agents.TryGetValue(id, out var state))
        {
            return state.Status;
        }
        throw new KeyNotFoundException($"Agent with id '{id}' was not found.");
    }

    public bool UpdateAgentStatus(string id, AgentStatus status, string? errorMessage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        while (_agents.TryGetValue(id, out var current))
        {
            var updated = current with
            {
                Status = status,
                LastError = errorMessage ?? (status == AgentStatus.Failed ? current.LastError : null),
                LastActiveAt = DateTimeOffset.UtcNow
            };

            if (_agents.TryUpdate(id, updated, current))
            {
                return true;
            }
        }

        return false;
    }

    public AgentState? GetAgentState(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _agents.TryGetValue(id, out var state) ? state : null;
    }

    public IReadOnlyList<AgentState> ListAgentStates()
    {
        return _agents.Values.ToList().AsReadOnly();
    }

    public async Task<AgentTaskResult> ExecuteTaskAsync(
        AgentTaskRequest request,
        Func<SubAgentSpec, IPythonRpcBridge, CancellationToken, Task<string>>? workDelegate = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskPrompt);

        if (!_agents.TryGetValue(request.AgentId, out var agentState))
        {
            throw new KeyNotFoundException($"Agent '{request.AgentId}' was not found.");
        }

        var taskId = $"task_{Guid.NewGuid():N}"[..16];
        var startTime = DateTimeOffset.UtcNow;

        // Transition to Running
        UpdateAgentState(request.AgentId, s => s with
        {
            Status = AgentStatus.Running,
            CurrentTaskId = taskId,
            StartedAt = startTime,
            LastActiveAt = startTime,
            LastError = null
        });

        try
        {
            var spec = new SubAgentSpec(
                Role: agentState.Config.Name,
                TaskPrompt: request.TaskPrompt,
                PreferredModel: agentState.Config.PreferredModel,
                MaxTokenBudget: agentState.Config.MaxTokenBudget);

            var execResult = await _orchestrator.SpawnAndExecuteAsync(spec, workDelegate, ct).ConfigureAwait(false);

            var finalStatus = execResult.IsSuccess ? AgentStatus.Completed : AgentStatus.Failed;

            UpdateAgentState(request.AgentId, s => s with
            {
                Status = finalStatus,
                CurrentTaskId = null,
                LastActiveAt = DateTimeOffset.UtcNow,
                TokensConsumed = s.TokensConsumed + execResult.TokensConsumed,
                LastError = execResult.IsSuccess ? null : execResult.CondensedOutput
            });

            return new AgentTaskResult(
                TaskId: taskId,
                AgentId: request.AgentId,
                IsSuccess: execResult.IsSuccess,
                Output: execResult.CondensedOutput,
                TokensConsumed: execResult.TokensConsumed,
                FinalStatus: finalStatus,
                ContextTranscript: execResult.ContextTranscript);
        }
        catch (Exception ex)
        {
            UpdateAgentState(request.AgentId, s => s with
            {
                Status = AgentStatus.Failed,
                CurrentTaskId = null,
                LastActiveAt = DateTimeOffset.UtcNow,
                LastError = ex.Message
            });

            return new AgentTaskResult(
                TaskId: taskId,
                AgentId: request.AgentId,
                IsSuccess: false,
                Output: ex.Message,
                TokensConsumed: 0,
                FinalStatus: AgentStatus.Failed,
                ContextTranscript: [$"[ERROR] {ex.Message}"]);
        }
    }

    private void UpdateAgentState(string agentId, Func<AgentState, AgentState> updateFn)
    {
        while (_agents.TryGetValue(agentId, out var current))
        {
            var updated = updateFn(current);
            if (_agents.TryUpdate(agentId, updated, current))
            {
                break;
            }
        }
    }
}
