namespace HUMofA.Scheduler;

/// <summary>
/// Status state machine for scheduled cron jobs.
/// </summary>
public enum CronJobStatus
{
    Active,
    Paused,
    Cancelled,
    Running
}

/// <summary>
/// Model representing a natural language scheduled background job.
/// </summary>
public class CronJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string NaturalSchedule { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public CronJobStatus Status { get; set; } = CronJobStatus.Active;
    public Func<CancellationToken, Task<string>> Action { get; set; } = _ => Task.FromResult(string.Empty);
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
    public string? LastResult { get; set; }
    public int RunCount { get; set; }
}
