using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HUMofA.Gateway;

/// <summary>
/// Risk tier classification for smart approval governance.
/// </summary>
public enum RiskTier
{
    /// <summary>
    /// Safe, read-only operations. Auto-executes without interactive intervention.
    /// </summary>
    Green,

    /// <summary>
    /// Low-risk or reversible actions. Executes after grace period unless cancelled.
    /// </summary>
    Yellow,

    /// <summary>
    /// Critical or irreversible actions (destructive fs ops, credentials, admin access, payments).
    /// Requires explicit interactive approval with screenshot card; times out to Denied.
    /// </summary>
    Red
}

/// <summary>
/// Request for approving an autonomous agent action.
/// </summary>
public record ApprovalRequest(
    string Id,
    string ActionName,
    RiskTier Tier,
    string Description,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string? ScreenshotBase64 = null,
    string? TargetChannel = null,
    TimeSpan? CustomTimeout = null)
{
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public static ApprovalRequest Create(
        string actionName,
        RiskTier tier,
        string description,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? screenshotBase64 = null,
        string? targetChannel = null,
        TimeSpan? customTimeout = null)
    {
        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        string token = $"appr_{Convert.ToHexString(tokenBytes).ToLowerInvariant()}";

        return new ApprovalRequest(
            token,
            actionName,
            tier,
            description,
            parameters,
            screenshotBase64,
            targetChannel,
            customTimeout
        );
    }
}

/// <summary>
/// Outcome of a smart approval evaluation.
/// </summary>
public record ApprovalDecision(
    string RequestId,
    bool IsApproved,
    string Reason,
    DateTime DecidedAt,
    string DecidedBy,
    RiskTier Tier)
{
    public static ApprovalDecision Approved(ApprovalRequest request, string reason, string decidedBy) =>
        new(request.Id, true, reason, DateTime.UtcNow, decidedBy, request.Tier);

    public static ApprovalDecision Denied(ApprovalRequest request, string reason, string decidedBy) =>
        new(request.Id, false, reason, DateTime.UtcNow, decidedBy, request.Tier);
}

/// <summary>
/// Governance engine providing 3-tier smart approval with grace periods, screenshot cards,
/// and interactive token-based authorization.
/// </summary>
public class SmartApproval
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pendingApprovals = new();
    private readonly IEventBus? _eventBus;

    public TimeSpan DefaultYellowGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan DefaultRedTimeout { get; init; } = TimeSpan.FromSeconds(120);

    private sealed class PendingApproval
    {
        public required ApprovalRequest Request { get; init; }
        public required TaskCompletionSource<ApprovalDecision> Tcs { get; init; }
        public required CancellationTokenSource TimeoutCts { get; init; }
    }

    public SmartApproval(IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

    /// <summary>
    /// Classifies an action into Green, Yellow, or Red risk tier based on action name,
    /// command patterns, and parameter inspection.
    /// </summary>
    public RiskTier ClassifyAction(string actionName, IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(actionName);

        string lowerAction = actionName.Trim().ToLowerInvariant();
        string combinedParams = parameters == null
            ? string.Empty
            : string.Join(" ", parameters.Values).ToLowerInvariant();

        // Check for Red Tier indicators (critical / destructive)
        if (lowerAction.Contains("format") ||
            lowerAction.Contains("delete_database") ||
            lowerAction.Contains("rmdir_recursive") ||
            lowerAction.Contains("wipe") ||
            lowerAction.Contains("admin") ||
            lowerAction.Contains("sudo") ||
            lowerAction.Contains("payment") ||
            lowerAction.Contains("checkout") ||
            lowerAction.Contains("send_email_blast") ||
            lowerAction.Contains("shutdown") ||
            lowerAction.Contains("reboot"))
        {
            return RiskTier.Red;
        }

        if (combinedParams.Contains("rm -rf") ||
            combinedParams.Contains("format ") ||
            combinedParams.Contains("del /s") ||
            ((combinedParams.Contains("rmdir /s") || combinedParams.Contains("rd /s")) && !combinedParams.Contains("temp") && !combinedParams.Contains("tmp")) ||
            combinedParams.Contains("drop database") ||
            combinedParams.Contains(".env") ||
            combinedParams.Contains("id_rsa") ||
            combinedParams.Contains("password") ||
            combinedParams.Contains("private_key") ||
            combinedParams.Contains("api_key") ||
            combinedParams.Contains("secret"))
        {
            return RiskTier.Red;
        }

        // Check for Yellow Tier indicators (local modification / reversible)
        if (lowerAction.Contains("write_file") ||
            lowerAction.Contains("edit_code") ||
            lowerAction.Contains("git_commit") ||
            lowerAction.Contains("open_browser") ||
            lowerAction.Contains("install_package") ||
            lowerAction.Contains("create_file") ||
            lowerAction.Contains("delete_file"))
        {
            return RiskTier.Yellow;
        }

        // Shell execution actions: must be at least RiskTier.Yellow unless strictly matching
        // a safe read-only whitelist (git status, git branch, git log, ls, dir, echo, pwd).
        if (IsShellAction(lowerAction))
        {
            string cmd = ExtractCommandString(parameters, combinedParams);
            if (IsSafeReadOnlyShellCommand(cmd))
            {
                return RiskTier.Green;
            }

            return RiskTier.Yellow;
        }

        // Green Tier (read-only / safe inspection)
        // e.g. som, move, click, list_windows, read_file, search, memory
        return RiskTier.Green;
    }

    private static bool IsShellAction(string lowerAction)
    {
        return lowerAction == "exec" ||
               lowerAction == "/exec" ||
               lowerAction == "terminal" ||
               lowerAction == "execute_terminal" ||
               lowerAction == "run_terminal" ||
               lowerAction == "bash" ||
               lowerAction == "execute_bash" ||
               lowerAction == "run_bash" ||
               lowerAction == "pwsh" ||
               lowerAction == "powershell" ||
               lowerAction == "sh" ||
               lowerAction == "shell" ||
               lowerAction == "cmd" ||
               lowerAction == "run_command" ||
               lowerAction == "execute_command" ||
               lowerAction.Contains("terminal") ||
               lowerAction.Contains("bash") ||
               lowerAction.Contains("pwsh") ||
               lowerAction.Contains("powershell") ||
               lowerAction.Contains("shell");
    }

    private static string ExtractCommandString(IReadOnlyDictionary<string, string>? parameters, string combinedParams)
    {
        if (parameters != null)
        {
            if (parameters.TryGetValue("cmd", out var cmd) && !string.IsNullOrWhiteSpace(cmd))
                return cmd.Trim();
            if (parameters.TryGetValue("command", out var command) && !string.IsNullOrWhiteSpace(command))
                return command.Trim();
            if (parameters.TryGetValue("input", out var input) && !string.IsNullOrWhiteSpace(input))
                return input.Trim();
        }
        return combinedParams.Trim();
    }

    public static bool IsSafeReadOnlyShellCommand(string rawCmd)
    {
        if (string.IsNullOrWhiteSpace(rawCmd)) return false;
        var cmd = rawCmd.Trim().ToLowerInvariant();

        // Must not contain piping, redirection, chaining, or command substitution
        if (cmd.Contains('|') || cmd.Contains(';') || cmd.Contains('&') ||
            cmd.Contains('>') || cmd.Contains('<') || cmd.Contains('`') ||
            cmd.Contains("$("))
        {
            return false;
        }

        // Whitelist checks
        if (cmd == "pwd" || cmd == "whoami")
        {
            return true;
        }

        if (cmd == "ls" || cmd.StartsWith("ls ") || cmd.StartsWith("ls-"))
        {
            return true;
        }

        if (cmd == "dir" || cmd.StartsWith("dir ") || cmd.StartsWith("dir/"))
        {
            return true;
        }

        if (cmd == "echo" || cmd.StartsWith("echo "))
        {
            return true;
        }

        if (cmd == "git status" || cmd.StartsWith("git status "))
        {
            return true;
        }

        if (cmd == "git log" || cmd.StartsWith("git log "))
        {
            return true;
        }

        if (cmd == "git branch" || cmd.StartsWith("git branch "))
        {
            // git branch delete (-d, -D) or move (-m) are mutating
            if (!cmd.Contains("-d") && !cmd.Contains("-m") && !cmd.Contains("--delete"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Requests approval for an action according to its classified or specified risk tier.
    /// </summary>
    public async Task<ApprovalDecision> RequestApprovalAsync(
        ApprovalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Notify event bus of incoming request
        if (_eventBus != null)
        {
            var notif = AgentMessage.Create(
                request.TargetChannel ?? "system",
                "SmartApproval",
                $"Approval requested for action '{request.ActionName}' [Tier: {request.Tier}]: {request.Description}",
                id: request.Id,
                metadata: new Dictionary<string, string>
                {
                    ["requestId"] = request.Id,
                    ["actionName"] = request.ActionName,
                    ["tier"] = request.Tier.ToString(),
                    ["hasScreenshot"] = (request.ScreenshotBase64 != null).ToString()
                }
            );
            await _eventBus.PublishAsync(notif, ct).ConfigureAwait(false);
        }

        switch (request.Tier)
        {
            case RiskTier.Green:
                return ApprovalDecision.Approved(
                    request,
                    "Auto-approved: Green tier safe read-only operation.",
                    "system:green-auto"
                );

            case RiskTier.Yellow:
                return await HandleYellowGracePeriodAsync(request, ct).ConfigureAwait(false);

            case RiskTier.Red:
                return await HandleRedInteractiveApprovalAsync(request, ct).ConfigureAwait(false);

            default:
                return ApprovalDecision.Denied(request, $"Unknown risk tier {request.Tier}", "system:error");
        }
    }

    private async Task<ApprovalDecision> HandleYellowGracePeriodAsync(
        ApprovalRequest request,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new PendingApproval
        {
            Request = request,
            Tcs = tcs,
            TimeoutCts = timeoutCts
        };

        _pendingApprovals[request.Id] = pending;
        var gracePeriod = request.CustomTimeout ?? DefaultYellowGracePeriod;

        try
        {
            // Auto-complete after grace period unless explicitly cancelled or resolved
            var timeoutTask = Task.Delay(gracePeriod, timeoutCts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

            if (completedTask == tcs.Task)
            {
                return await tcs.Task.ConfigureAwait(false);
            }

            // Check if cancellation was triggered
            if (ct.IsCancellationRequested || timeoutCts.IsCancellationRequested || completedTask.IsCanceled)
            {
                return ApprovalDecision.Denied(request, "Operation was cancelled during grace period.", "system:cancelled");
            }

            // Await timeoutTask to ensure any OperationCanceledException propagates
            await timeoutTask.ConfigureAwait(false);

            // Grace period elapsed without cancellation -> Auto-Approved
            return ApprovalDecision.Approved(
                request,
                $"Auto-approved after {gracePeriod.TotalSeconds:F1}s grace period without cancellation.",
                "system:yellow-grace-elapsed"
            );
        }
        catch (OperationCanceledException) when (!tcs.Task.IsCompleted)
        {
            return ApprovalDecision.Denied(request, "Operation was cancelled during grace period.", "system:cancelled");
        }
        finally
        {
            _pendingApprovals.TryRemove(request.Id, out _);
            timeoutCts.Dispose();
        }
    }

    private async Task<ApprovalDecision> HandleRedInteractiveApprovalAsync(
        ApprovalRequest request,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new PendingApproval
        {
            Request = request,
            Tcs = tcs,
            TimeoutCts = timeoutCts
        };

        _pendingApprovals[request.Id] = pending;
        var timeout = request.CustomTimeout ?? DefaultRedTimeout;

        try
        {
            var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

            if (completedTask == tcs.Task)
            {
                return await tcs.Task.ConfigureAwait(false);
            }

            // Check if cancellation was triggered
            if (ct.IsCancellationRequested || timeoutCts.IsCancellationRequested || completedTask.IsCanceled)
            {
                return ApprovalDecision.Denied(request, "Approval request was cancelled via cancellation token.", "system:cancelled");
            }

            await timeoutTask.ConfigureAwait(false);

            // Red tier timed out -> Default to DENIED for safety
            return ApprovalDecision.Denied(
                request,
                $"Approval timed out after {timeout.TotalSeconds:F1}s without user confirmation. Defaulting to DENIED for safety.",
                "system:red-timeout-denied"
            );
        }
        catch (OperationCanceledException) when (!tcs.Task.IsCompleted)
        {
            return ApprovalDecision.Denied(request, "Approval request was cancelled via cancellation token.", "system:cancelled");
        }
        finally
        {
            _pendingApprovals.TryRemove(request.Id, out _);
            timeoutCts.Dispose();
        }
    }

    /// <summary>
    /// Submits an interactive approval or denial for a pending request by token.
    /// </summary>
    public bool SubmitApproval(string token, bool approved, string? reason = null, string? approvedBy = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (!_pendingApprovals.TryGetValue(token, out var pending))
        {
            return false;
        }

        var decision = approved
            ? ApprovalDecision.Approved(
                pending.Request,
                reason ?? "Explicitly approved by user.",
                approvedBy ?? "user:interactive"
            )
            : ApprovalDecision.Denied(
                pending.Request,
                reason ?? "Explicitly denied by user.",
                approvedBy ?? "user:interactive"
            );

        return pending.Tcs.TrySetResult(decision);
    }

    /// <summary>
    /// Cancels a pending Yellow Tier grace period request, aborting the action.
    /// </summary>
    public bool CancelYellowGracePeriod(string token, string? reason = null)
    {
        return SubmitApproval(token, approved: false, reason: reason ?? "Yellow tier cancelled during grace period.");
    }

    /// <summary>
    /// Returns all currently pending approval requests.
    /// </summary>
    public IReadOnlyList<ApprovalRequest> GetPendingRequests()
    {
        return _pendingApprovals.Values.Select(p => p.Request).ToList();
    }
}
