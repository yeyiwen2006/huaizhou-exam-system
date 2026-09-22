using Exam.Application.Versioning;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;
using Npgsql;

namespace Exam.Server.Runtime;

public sealed class PostgresScheduleProcessor(
    NpgsqlDataSource dataSource,
    IExamVersioningService versioning,
    ActiveNodeGuard activeNode,
    TimeProvider timeProvider,
    ILogger<PostgresScheduleProcessor> logger)
{
    public async Task<int> ExecuteDueAsync(CancellationToken cancellationToken = default)
    {
        var active = await activeNode.CheckAsync(cancellationToken);
        if (!active.Allowed)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var due = new List<DueSchedule>();
        await using (var command = dataSource.CreateCommand("""
            SELECT exam_id, schedule_version, lifecycle
              FROM exam.version_state
             WHERE (lifecycle = 'Scheduled' AND starts_at <= @now)
                OR (lifecycle = 'Running' AND ends_at <= @now)
             ORDER BY CASE lifecycle WHEN 'Running' THEN 0 ELSE 1 END,
                      CASE lifecycle WHEN 'Running' THEN ends_at ELSE starts_at END,
                      exam_id
             LIMIT 100;
            """))
        {
            command.Parameters.AddWithValue("now", now);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(new DueSchedule(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2) == "Scheduled"
                        ? ScheduleTriggerKind.Start
                        : ScheduleTriggerKind.End));
            }
        }

        var applied = 0;
        foreach (var schedule in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = versioning.ExecuteScheduleTrigger(new ExecuteScheduleTriggerRequest(
                schedule.ExamId,
                schedule.ScheduleVersion,
                schedule.Trigger,
                now,
                $"schedule-timer:{schedule.ExamId}:{schedule.ScheduleVersion}:{schedule.Trigger}"));
            if (result.Succeeded && result.Value?.Applied == true)
            {
                applied++;
                continue;
            }

            if (!result.Succeeded)
            {
                logger.LogWarning(
                    "Schedule trigger {Trigger} for exam {ExamId} was not applied: {Code} {Reason}",
                    schedule.Trigger,
                    schedule.ExamId,
                    result.ErrorCode,
                    result.Error);
            }
        }

        return applied;
    }

    private sealed record DueSchedule(
        string ExamId,
        long ScheduleVersion,
        ScheduleTriggerKind Trigger);
}

public sealed class PostgresScheduleWorker(
    PostgresScheduleProcessor processor,
    ILogger<PostgresScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var applied = await processor.ExecuteDueAsync(stoppingToken);
                await Task.Delay(applied == 100 ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromMilliseconds(500), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled exam transition processing failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
