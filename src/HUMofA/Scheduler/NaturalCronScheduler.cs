using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using HUMofA.Gateway;

namespace HUMofA.Scheduler;

/// <summary>
/// Parsed representation of a schedule derived from natural language or standard cron syntax.
/// </summary>
public record ParsedSchedule(
    string NaturalExpression,
    string CronExpression,
    Func<DateTime, DateTime> NextOccurrenceCalculator
);

/// <summary>
/// Natural Language Cron Scheduler capable of parsing conversational time specifications,
/// managing job lifecycles, executing unattended background tasks, and reporting to CentralEventBus.
/// </summary>
public class NaturalCronScheduler
{
    private readonly ConcurrentDictionary<string, CronJob> _jobs = new();
    private readonly IEventBus? _eventBus;
    private CancellationTokenSource? _schedulerCts;
    private Task? _schedulerTask;

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    public NaturalCronScheduler(IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

    /// <summary>
    /// Translates conversational phrasing or standard cron syntax into a ParsedSchedule.
    /// </summary>
    public static ParsedSchedule Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string text = expression.Trim().ToLowerInvariant();

        // 1. "every X minutes" / "every minute"
        var minMatch = Regex.Match(text, @"^every\s+(\d+)?\s*minutes?$");
        if (minMatch.Success)
        {
            int interval = minMatch.Groups[1].Success ? int.Parse(minMatch.Groups[1].Value) : 1;
            interval = Math.Max(1, Math.Min(59, interval));
            string cron = interval == 1 ? "* * * * *" : $"*/{interval} * * * *";

            return new ParsedSchedule(expression, cron, fromUtc =>
            {
                var dt = fromUtc.AddSeconds(-fromUtc.Second).AddMilliseconds(-fromUtc.Millisecond);
                // Step forward until minute % interval == 0 and dt > fromUtc
                do
                {
                    dt = dt.AddMinutes(1);
                } while (dt.Minute % interval != 0 || dt <= fromUtc);
                return dt;
            });
        }

        // 2. "every X hours" / "every hour"
        var hrMatch = Regex.Match(text, @"^every\s+(\d+)?\s*hours?$");
        if (hrMatch.Success)
        {
            int interval = hrMatch.Groups[1].Success ? int.Parse(hrMatch.Groups[1].Value) : 1;
            interval = Math.Max(1, Math.Min(23, interval));
            string cron = interval == 1 ? "0 * * * *" : $"0 */{interval} * * *";

            return new ParsedSchedule(expression, cron, fromUtc =>
            {
                var dt = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);
                do
                {
                    dt = dt.AddHours(1);
                } while (dt.Hour % interval != 0 || dt <= fromUtc);
                return dt;
            });
        }

        // 3. "daily at HH:mm" / "every day at HH:mm" / "every morning at HH:mm" / "every evening at HH:mm"
        var dailyMatch = Regex.Match(text, @"^(?:daily|every\s+day|every\s+morning|every\s+evening)\s+at\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$");
        if (dailyMatch.Success)
        {
            int hour = int.Parse(dailyMatch.Groups[1].Value);
            int minute = dailyMatch.Groups[2].Success ? int.Parse(dailyMatch.Groups[2].Value) : 0;
            string ampm = dailyMatch.Groups[3].Value;

            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;

            string cron = $"{minute} {hour} * * *";

            return new ParsedSchedule(expression, cron, fromUtc =>
            {
                var target = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, hour, minute, 0, DateTimeKind.Utc);
                if (target <= fromUtc)
                {
                    target = target.AddDays(1);
                }
                return target;
            });
        }

        // 4. "every [day of week] at HH:mm" (e.g. "every monday at 10:00", "every sunday midnight")
        var dowMidnightMatch = Regex.Match(text, @"^every\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)(?:\s+(?:at\s+)?midnight)?$");
        if (dowMidnightMatch.Success && text.Contains("midnight"))
        {
            var dow = ParseDayOfWeek(dowMidnightMatch.Groups[1].Value);
            int cronDow = (int)dow;
            string cron = $"0 0 * * {cronDow}";

            return new ParsedSchedule(expression, cron, fromUtc =>
            {
                var dt = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, 0, 0, 0, DateTimeKind.Utc);
                do
                {
                    dt = dt.AddDays(1);
                } while (dt.DayOfWeek != dow || dt <= fromUtc);
                return dt;
            });
        }

        var dowMatch = Regex.Match(text, @"^every\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\s+at\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$");
        if (dowMatch.Success)
        {
            var dow = ParseDayOfWeek(dowMatch.Groups[1].Value);
            int hour = int.Parse(dowMatch.Groups[2].Value);
            int minute = dowMatch.Groups[3].Success ? int.Parse(dowMatch.Groups[3].Value) : 0;
            string ampm = dowMatch.Groups[4].Value;

            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;

            int cronDow = (int)dow;
            string cron = $"{minute} {hour} * * {cronDow}";

            return new ParsedSchedule(expression, cron, fromUtc =>
            {
                var dt = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, hour, minute, 0, DateTimeKind.Utc);
                while (dt <= fromUtc || dt.DayOfWeek != dow)
                {
                    dt = dt.AddDays(1);
                }
                return dt;
            });
        }

        // 5. "weekdays at HH:mm" / "every weekday at HH:mm"
        var weekdayMatch = Regex.Match(text, @"^(?:weekdays|every\s+weekday)\s+at\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$");
        if (weekdayMatch.Success)
        {
            int hour = int.Parse(weekdayMatch.Groups[1].Value);
            int minute = weekdayMatch.Groups[2].Success ? int.Parse(weekdayMatch.Groups[2].Value) : 0;
            string ampm = weekdayMatch.Groups[3].Value;

            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;

            string cron = $"{minute} {hour} * * 1-5";

            return new ParsedSchedule(expression, cron, fromUtc =>
            {
                var dt = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, hour, minute, 0, DateTimeKind.Utc);
                while (dt <= fromUtc || dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday)
                {
                    dt = dt.AddDays(1);
                }
                return dt;
            });
        }

        // 6. Fallback: Parse as standard 5-part cron expression (e.g. "*/5 * * * *", "0 9 * * *")
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5 && parts.All(IsValidCronPart))
        {
            return ParseStandardCron(expression, parts);
        }

        throw new ArgumentException($"Unable to parse natural cron schedule expression: '{expression}'.");
    }

    private static bool IsValidCronPart(string part)
    {
        if (part == "*" || part == "?") return true;
        return Regex.IsMatch(part, @"^(\*/\d+|\d+|\d+-\d+|\d+(?:,\d+)*)$");
    }

    private static DayOfWeek ParseDayOfWeek(string day) => day.ToLowerInvariant() switch
    {
        "sunday" => DayOfWeek.Sunday,
        "monday" => DayOfWeek.Monday,
        "tuesday" => DayOfWeek.Tuesday,
        "wednesday" => DayOfWeek.Wednesday,
        "thursday" => DayOfWeek.Thursday,
        "friday" => DayOfWeek.Friday,
        "saturday" => DayOfWeek.Saturday,
        _ => throw new ArgumentException($"Invalid day of week: {day}")
    };

    private static ParsedSchedule ParseStandardCron(string original, string[] parts)
    {
        string minPart = parts[0];
        string hrPart = parts[1];
        string domPart = parts[2];
        string monPart = parts[3];
        string dowPart = parts[4];

        return new ParsedSchedule(original, string.Join(" ", parts), fromUtc =>
        {
            var cursor = fromUtc.AddSeconds(-fromUtc.Second).AddMilliseconds(-fromUtc.Millisecond).AddMinutes(1);
            // Search up to 366 days ahead minute-by-minute
            var limit = cursor.AddDays(366);

            while (cursor < limit)
            {
                if (MatchesCronPart(cursor.Minute, minPart) &&
                    MatchesCronPart(cursor.Hour, hrPart) &&
                    MatchesCronPart(cursor.Day, domPart) &&
                    MatchesCronPart(cursor.Month, monPart) &&
                    MatchesDowPart((int)cursor.DayOfWeek, dowPart))
                {
                    return cursor;
                }

                cursor = cursor.AddMinutes(1);
            }

            return fromUtc.AddDays(1);
        });
    }

    private static bool MatchesCronPart(int value, string part)
    {
        if (part == "*") return true;

        if (part.StartsWith("*/"))
        {
            if (int.TryParse(part[2..], out int step) && step > 0)
            {
                return value % step == 0;
            }
        }

        if (int.TryParse(part, out int exact))
        {
            return value == exact;
        }

        if (part.Contains(','))
        {
            return part.Split(',').Any(p => MatchesCronPart(value, p.Trim()));
        }

        if (part.Contains('-'))
        {
            var range = part.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out int start) && int.TryParse(range[1], out int end))
            {
                return value >= start && value <= end;
            }
        }

        return false;
    }

    private static bool MatchesDowPart(int dow, string part)
    {
        if (part == "*" || part == "?") return true;
        if (part == "1-5") return dow >= 1 && dow <= 5;
        return MatchesCronPart(dow, part);
    }

    /// <summary>
    /// Calculates the next occurrence for a given natural schedule from a reference time.
    /// </summary>
    public static DateTime GetNextOccurrence(string schedule, DateTime? fromUtc = null)
    {
        var parsed = Parse(schedule);
        return parsed.NextOccurrenceCalculator(fromUtc ?? DateTime.UtcNow);
    }

    /// <summary>
    /// Registers a new background scheduled job.
    /// </summary>
    public CronJob RegisterJob(string name, string naturalSchedule, Func<CancellationToken, Task<string>> action)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(naturalSchedule);
        ArgumentNullException.ThrowIfNull(action);

        var parsed = Parse(naturalSchedule);
        var now = DateTime.UtcNow;

        var job = new CronJob
        {
            Name = name,
            NaturalSchedule = naturalSchedule,
            CronExpression = parsed.CronExpression,
            Status = CronJobStatus.Active,
            Action = action,
            CreatedAt = now,
            NextRun = parsed.NextOccurrenceCalculator(now)
        };

        _jobs[job.Id] = job;
        return job;
    }

    public IReadOnlyList<CronJob> ListJobs() => _jobs.Values.ToList();

    public CronJob? GetJob(string id) => _jobs.GetValueOrDefault(id);

    public bool PauseJob(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.Status = CronJobStatus.Paused;
            return true;
        }
        return false;
    }

    public bool ResumeJob(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.Status = CronJobStatus.Active;
            var parsed = Parse(job.NaturalSchedule);
            job.NextRun = parsed.NextOccurrenceCalculator(DateTime.UtcNow);
            return true;
        }
        return false;
    }

    public bool CancelJob(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.Status = CronJobStatus.Cancelled;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Manually triggers immediate execution of a job.
    /// </summary>
    public async Task<string> TriggerNowAsync(string id, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            throw new KeyNotFoundException($"Cron job '{id}' not found.");
        }

        return await ExecuteJobAsync(job, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the background scheduler loop.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_schedulerTask != null)
        {
            return Task.CompletedTask;
        }

        _schedulerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _schedulerTask = Task.Run(() => RunSchedulerLoopAsync(_schedulerCts.Token), _schedulerCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background scheduler loop.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_schedulerCts != null)
        {
            _schedulerCts.Cancel();
            if (_schedulerTask != null)
            {
                try
                {
                    await _schedulerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            _schedulerCts.Dispose();
            _schedulerCts = null;
            _schedulerTask = null;
        }
    }

    private async Task RunSchedulerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var job in _jobs.Values)
                {
                    if (job.Status == CronJobStatus.Active && job.NextRun.HasValue && job.NextRun.Value <= now)
                    {
                        // Fire job asynchronously without blocking check of next jobs
                        _ = ExecuteJobAsync(job, ct);
                    }
                }

                await Task.Delay(PollingInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NaturalCronScheduler] Error in scheduler loop: {ex.Message}");
            }
        }
    }

    private async Task<string> ExecuteJobAsync(CronJob job, CancellationToken ct)
    {
        job.Status = CronJobStatus.Running;
        string result = string.Empty;

        try
        {
            result = await job.Action(ct).ConfigureAwait(false);
            job.LastResult = result;
        }
        catch (Exception ex)
        {
            result = $"Error: {ex.Message}";
            job.LastResult = result;
        }
        finally
        {
            job.LastRun = DateTime.UtcNow;
            job.RunCount++;

            // If not cancelled while running, restore active and compute next occurrence
            if (job.Status == CronJobStatus.Running)
            {
                job.Status = CronJobStatus.Active;
                try
                {
                    var parsed = Parse(job.NaturalSchedule);
                    job.NextRun = parsed.NextOccurrenceCalculator(job.LastRun.Value);
                }
                catch
                {
                    job.NextRun = job.LastRun.Value.AddHours(1);
                }
            }

            // Report execution result to CentralEventBus
            if (_eventBus != null)
            {
                var report = AgentMessage.Create(
                    channel: "scheduler",
                    sender: "NaturalCronScheduler",
                    content: $"Job '{job.Name}' (ID: {job.Id}) executed. Result: {result}",
                    metadata: new Dictionary<string, string>
                    {
                        ["jobId"] = job.Id,
                        ["jobName"] = job.Name,
                        ["runCount"] = job.RunCount.ToString(),
                        ["lastResult"] = result
                    }
                );
                await _eventBus.PublishAsync(report, ct).ConfigureAwait(false);
            }
        }

        return result;
    }
}
