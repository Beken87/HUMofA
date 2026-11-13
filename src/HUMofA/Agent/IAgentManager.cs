using HUMofA.SubAgents;

namespace HUMofA.Agent;

/// <summary>
/// Contract for managing agent configurations, lifecycles, and sub-agent execution.
/// </summary>
public interface IAgentManager
{
    AgentConfig RegisterAgent(AgentConfig config);

    AgentConfig? GetAgent(string id);

    IReadOnlyList<AgentConfig> ListAgents();

    bool UpdateAgentConfig(AgentConfig config);

    bool DeleteAgent(string id);

    AgentStatus GetAgentStatus(string id);

    bool UpdateAgentStatus(string id, AgentStatus status, string? errorMessage = null);

    AgentState? GetAgentState(string id);

    IReadOnlyList<AgentState> ListAgentStates();

    Task<AgentTaskResult> ExecuteTaskAsync(
        AgentTaskRequest request,
        Func<SubAgentSpec, IPythonRpcBridge, CancellationToken, Task<string>>? workDelegate = null,
        CancellationToken ct = default);
}
