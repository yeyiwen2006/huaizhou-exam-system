using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Domain.Commands;

namespace Exam.Application.Commands;

public interface IParticipationAccessReader
{
    ValueTask<ParticipationAccessSnapshot?> FindAsync(
        string participationId,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryParticipationAccessRegistry : IParticipationAccessReader
{
    private readonly ConcurrentDictionary<string, ParticipationAccessSnapshot> _states =
        new(StringComparer.Ordinal);

    public void Set(ParticipationAccessSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _states[state.ParticipationId] = state;
    }

    public ValueTask<ParticipationAccessSnapshot?> FindAsync(
        string participationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states.TryGetValue(participationId, out var state);
        return ValueTask.FromResult(state);
    }
}

public sealed class ReliableCommandService
{
    private readonly ServerNodeEnvelope _server;
    private readonly ICommandEventStore _store;
    private readonly IParticipationAccessReader _accessReader;
    private readonly TimeProvider _timeProvider;

    public ReliableCommandService(
        ServerNodeEnvelope server,
        ICommandEventStore store,
        IParticipationAccessReader accessReader,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accessReader);
        if (string.IsNullOrWhiteSpace(server.ServerId) ||
            string.IsNullOrWhiteSpace(server.NodeId) ||
            server.ServerEpoch < 1)
        {
            throw new ArgumentException("服务器身份和代次必须有效。", nameof(server));
        }

        _server = server;
        _store = store;
        _accessReader = accessReader;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ServerTimeSnapshot GetServerTime() =>
        new(_timeProvider.GetUtcNow(), _server);

    public async ValueTask<CommandOperationResult<CommandEventView>> CreateAsync(
        CreateCommandEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationError = ReliableCommandPolicy.Validate(request);
        if (validationError is not null)
        {
            return CommandOperationResult<CommandEventView>.Failure(
                CommandErrorCode.InvalidRequest,
                validationError);
        }

        try
        {
            var replay = await _store.FindByCreateRequestAsync(request.RequestId, cancellationToken);
            if (replay is not null)
            {
                return CommandOperationResult<CommandEventView>.Success(
                    ToView(replay),
                    "返回原指令创建结果。");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CommandOperationResult<CommandEventView>.Failure(
                CommandErrorCode.PersistenceFailed,
                "查询原指令结果失败，请保留原记录并稍后重试。");
        }

        if (request.ParticipationId is not null)
        {
            var authorization = await _accessReader.FindAsync(request.ParticipationId, cancellationToken);
            var accessError = ValidateTarget(request, authorization);
            if (accessError is not null)
            {
                return accessError;
            }
        }

        var now = _timeProvider.GetUtcNow();
        var metadata = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(request.SafeMetadata, StringComparer.Ordinal));
        var persistentEvent = new PersistentCommandEvent(
            request.EventId,
            request.ExamId,
            request.ParticipationId,
            request.EntryVersion,
            request.EventType,
            request.StateVersion,
            request.TargetDeviceId,
            request.TargetAuthorizationVersion,
            request.ResourceVersion,
            request.ProtectedUnlockReference,
            metadata,
            now,
            now,
            0,
            null);

        try
        {
            var stored = await _store.CreateOrGetAsync(
                request.RequestId,
                persistentEvent,
                cancellationToken);
            if (stored.Status == CommandCreatePersistenceStatus.EventIdConflict)
            {
                return CommandOperationResult<CommandEventView>.Failure(
                    CommandErrorCode.VersionConflict,
                    "事件标识已被另一条持久指令使用。");
            }

            return CommandOperationResult<CommandEventView>.Success(
                ToView(stored.Event),
                stored.Status == CommandCreatePersistenceStatus.RequestReplay
                    ? "返回原指令创建结果。"
                    : "指令已持久保存。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CommandOperationResult<CommandEventView>.Failure(
                CommandErrorCode.PersistenceFailed,
                "指令未能持久保存，请保留原记录并稍后重试。");
        }
    }

    public async ValueTask<CommandOperationResult<IReadOnlyList<CommandDelivery>>> GetDueForDeviceAsync(
        string participationId,
        long entryVersion,
        string deviceId,
        long authorizationVersion,
        string serverId,
        long serverEpoch,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(serverId, _server.ServerId, StringComparison.Ordinal) ||
            serverEpoch != _server.ServerEpoch)
        {
            return CommandOperationResult<IReadOnlyList<CommandDelivery>>.Failure(
                CommandErrorCode.StaleServerEpoch,
                "服务器身份或服务代次不匹配。请先重新获取当前状态。");
        }

        var authorization = await _accessReader.FindAsync(participationId, cancellationToken);
        if (!Matches(authorization, entryVersion, deviceId, authorizationVersion))
        {
            return CommandOperationResult<IReadOnlyList<CommandDelivery>>.Failure(
                CommandErrorCode.AuthorizationRevoked,
                "设备授权或参加考试版本已失效。");
        }

        var now = _timeProvider.GetUtcNow();
        IReadOnlyList<PersistentCommandEvent> candidates;
        try
        {
            candidates = await _store.ClaimDueForDeviceAsync(
                participationId,
                entryVersion,
                deviceId,
                authorizationVersion,
                now,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CommandOperationResult<IReadOnlyList<CommandDelivery>>.Failure(
                CommandErrorCode.PersistenceFailed,
                "读取待处理指令失败，请保留原记录并稍后重试。");
        }

        var deliveries = new List<CommandDelivery>();
        foreach (var candidate in candidates)
        {
            if (!CanDeliver(candidate, authorization!))
            {
                continue;
            }

            var nextAttempt = candidate.DeliveryAttempts + 1;
            var updated = await _store.RecordDeliveryAttemptAsync(
                candidate.EventId,
                now,
                now + ReliableCommandPolicy.RetryDelay(nextAttempt),
                cancellationToken);
            if (updated is not null)
            {
                deliveries.Add(ToDelivery(updated));
            }
        }

        return CommandOperationResult<IReadOnlyList<CommandDelivery>>.Success(deliveries);
    }

    public async ValueTask<CommandOperationResult<CommandDelivery>> ResendAsync(
        string eventId,
        string participationId,
        long entryVersion,
        string deviceId,
        long authorizationVersion,
        CancellationToken cancellationToken = default)
    {
        var commandEvent = await _store.FindAsync(eventId, cancellationToken);
        if (commandEvent is null)
        {
            return CommandOperationResult<CommandDelivery>.Failure(
                CommandErrorCode.EventNotFound,
                "未找到指定指令。");
        }

        var authorization = await _accessReader.FindAsync(participationId, cancellationToken);
        if (!Matches(authorization, entryVersion, deviceId, authorizationVersion) ||
            !Targets(commandEvent, participationId, entryVersion, deviceId, authorizationVersion))
        {
            return CommandOperationResult<CommandDelivery>.Failure(
                CommandErrorCode.AuthorizationRevoked,
                "只能补发给当前有效授权设备。");
        }

        if (!CanDeliver(commandEvent, authorization!, manualResend: true))
        {
            return CommandOperationResult<CommandDelivery>.Failure(
                commandEvent.EventType == CommandEventType.Start
                    ? CommandErrorCode.KeyNotReleased
                    : CommandErrorCode.VersionConflict,
                "该指令当前已不再有效。");
        }

        var now = _timeProvider.GetUtcNow();
        var nextAttempt = commandEvent.DeliveryAttempts + 1;
        var updated = await _store.RecordManualDeliveryAttemptAsync(
            eventId,
            now,
            now + ReliableCommandPolicy.RetryDelay(nextAttempt),
            cancellationToken);
        return updated is null
            ? CommandOperationResult<CommandDelivery>.Failure(CommandErrorCode.EventNotFound, "未找到指定指令。")
            : CommandOperationResult<CommandDelivery>.Success(ToDelivery(updated), "已按原事件标识补发。");
    }

    public async ValueTask<CommandOperationResult<CommandAcknowledgement>> AcknowledgeAsync(
        AcknowledgeCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.EventId) ||
            string.IsNullOrWhiteSpace(request.ParticipationId) ||
            string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return CommandOperationResult<CommandAcknowledgement>.Failure(
                CommandErrorCode.InvalidRequest,
                "确认请求缺少必要标识。");
        }

        PersistentCommandEvent? commandEvent;
        try
        {
            commandEvent = await _store.FindAsync(request.EventId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CommandOperationResult<CommandAcknowledgement>.Failure(
                CommandErrorCode.PersistenceFailed,
                "查询指令失败，请保留原记录并稍后重试。");
        }

        if (commandEvent is null)
        {
            return CommandOperationResult<CommandAcknowledgement>.Failure(
                CommandErrorCode.EventNotFound,
                "未找到指定指令。");
        }

        if (!Targets(
                commandEvent,
                request.ParticipationId,
                request.EntryVersion,
                request.DeviceId,
                request.AuthorizationVersion))
        {
            return CommandOperationResult<CommandAcknowledgement>.Failure(
                CommandErrorCode.AuthorizationRevoked,
                "确认请求与原指令目标不匹配。");
        }

        try
        {
            var replay = await _store.FindAcknowledgementByRequestAsync(
                request.RequestId,
                request.EventId,
                cancellationToken);
            if (replay is not null)
            {
                return CommandOperationResult<CommandAcknowledgement>.Success(
                    replay,
                    "返回原确认结果。");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CommandOperationResult<CommandAcknowledgement>.Failure(
                CommandErrorCode.PersistenceFailed,
                "查询原确认结果失败，请保留原记录并稍后重试。");
        }

        var authorization = await _accessReader.FindAsync(request.ParticipationId, cancellationToken);
        if (!Matches(authorization, request.EntryVersion, request.DeviceId, request.AuthorizationVersion))
        {
            return CommandOperationResult<CommandAcknowledgement>.Failure(
                CommandErrorCode.AuthorizationRevoked,
                "旧授权不能确认当前指令。");
        }

        if (commandEvent.EventType == CommandEventType.PaperUpdated &&
            request.ProcessingResult == CommandProcessingResult.Applied &&
            (request.ActualPaperVersion is null || request.ActualPaperVersion < commandEvent.ResourceVersion))
        {
            return CommandOperationResult<CommandAcknowledgement>.Failure(
                CommandErrorCode.VersionConflict,
                "客户端尚未实际切换到要求的试卷版本。");
        }

        var acknowledgement = new CommandAcknowledgement(
            request.EventId,
            request.RequestId,
            request.ProcessingResult,
            request.ActualPaperVersion,
            request.FailureReason,
            _timeProvider.GetUtcNow());
        try
        {
            var stored = await _store.AcknowledgeAsync(
                request.RequestId,
                request.EventId,
                acknowledgement,
                cancellationToken);
            if (stored.Status == CommandAckPersistenceStatus.EventNotFound || stored.Acknowledgement is null)
            {
                return CommandOperationResult<CommandAcknowledgement>.Failure(
                    CommandErrorCode.EventNotFound,
                    "未找到指定指令。");
            }

            if (stored.Status == CommandAckPersistenceStatus.RequestIdConflict)
            {
                return CommandOperationResult<CommandAcknowledgement>.Failure(
                    CommandErrorCode.VersionConflict,
                    "requestId 已用于另一条指令确认。");
            }

            return CommandOperationResult<CommandAcknowledgement>.Success(
                stored.Acknowledgement,
                stored.Status == CommandAckPersistenceStatus.RequestReplay
                    ? "返回原确认结果。"
                    : "确认结果已持久保存。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CommandOperationResult<CommandAcknowledgement>.Failure(
                CommandErrorCode.PersistenceFailed,
                "确认结果未能持久保存，请保留原记录并稍后重试。");
        }
    }

    private static CommandOperationResult<CommandEventView>? ValidateTarget(
        CreateCommandEventRequest request,
        ParticipationAccessSnapshot? authorization)
    {
        if (!Matches(
                authorization,
                request.EntryVersion,
                request.TargetDeviceId!,
                request.TargetAuthorizationVersion!.Value) ||
            !string.Equals(authorization!.ExamId, request.ExamId, StringComparison.Ordinal))
        {
            return CommandOperationResult<CommandEventView>.Failure(
                CommandErrorCode.AuthorizationRevoked,
                "指令目标不是当前有效授权设备。");
        }

        if (request.EventType == CommandEventType.Start &&
            authorization.ExamLifecycle != ExamLifecycle.Running)
        {
            return CommandOperationResult<CommandEventView>.Failure(
                CommandErrorCode.KeyNotReleased,
                "考试尚未开始，不能创建含解锁材料的开始指令。");
        }

        if (request.EventType == CommandEventType.End &&
            authorization.ExamLifecycle != ExamLifecycle.Ended)
        {
            return CommandOperationResult<CommandEventView>.Failure(
                CommandErrorCode.VersionConflict,
                "考试尚未进入结束状态。");
        }

        return null;
    }

    private static bool Matches(
        ParticipationAccessSnapshot? authorization,
        long entryVersion,
        string deviceId,
        long authorizationVersion) =>
        authorization is not null &&
        authorization.EntryVersion == entryVersion &&
        authorization.AuthorizationVersion == authorizationVersion &&
        string.Equals(authorization.AuthorizedDeviceId, deviceId, StringComparison.Ordinal);

    private static bool Targets(
        PersistentCommandEvent commandEvent,
        string participationId,
        long entryVersion,
        string deviceId,
        long authorizationVersion) =>
        string.Equals(commandEvent.ParticipationId, participationId, StringComparison.Ordinal) &&
        commandEvent.EntryVersion == entryVersion &&
        string.Equals(commandEvent.TargetDeviceId, deviceId, StringComparison.Ordinal) &&
        commandEvent.TargetAuthorizationVersion == authorizationVersion;

    private static bool CanDeliver(
        PersistentCommandEvent commandEvent,
        ParticipationAccessSnapshot authorization, bool manualResend = false)
    {
        if (commandEvent.EventType is CommandEventType.Start or CommandEventType.End &&
            commandEvent.StateVersion < authorization.ReopenedAtScheduleVersion)
            return false;
        // 已执行 END 只说明停止答题，不能证明答卷已回收。考务可请求当前设备补传原固定答卷。
        var reupload = manualResend && commandEvent.EventType == CommandEventType.End &&
            authorization.ExamLifecycle == ExamLifecycle.Ended &&
            authorization.BusinessState is ParticipationBusinessState.Waiting or ParticipationBusinessState.Answering;
        if (commandEvent.Acknowledgement?.ProcessingResult == CommandProcessingResult.RejectedAsStale ||
            commandEvent.Acknowledgement?.ProcessingResult == CommandProcessingResult.Applied && !reupload)
        {
            return false;
        }

        return commandEvent.EventType switch
        {
            CommandEventType.Start => authorization.ExamLifecycle == ExamLifecycle.Running &&
                                      authorization.BusinessState is ParticipationBusinessState.Waiting or
                                          ParticipationBusinessState.Answering,
            CommandEventType.End => authorization.ExamLifecycle == ExamLifecycle.Ended &&
                (!manualResend || authorization.BusinessState != ParticipationBusinessState.Submitted),
            _ => true
        };
    }

    private CommandDelivery ToDelivery(PersistentCommandEvent commandEvent) =>
        new(
            _server,
            commandEvent.EventId,
            commandEvent.ExamId,
            commandEvent.ParticipationId!,
            commandEvent.EntryVersion,
            commandEvent.EventType,
            commandEvent.StateVersion,
            commandEvent.TargetDeviceId!,
            commandEvent.TargetAuthorizationVersion!.Value,
            commandEvent.ResourceVersion,
            commandEvent.EventType == CommandEventType.Start
                ? commandEvent.ProtectedUnlockReference
                : null,
            commandEvent.Metadata,
            commandEvent.CreatedAt,
            commandEvent.DeliveryAttempts)
        {
            NewScheduledStart = ReadScheduleTime(commandEvent, "scheduledStart"),
            NewScheduledEnd = ReadScheduleTime(commandEvent, "scheduledEnd")
        };

    private static DateTimeOffset? ReadScheduleTime(PersistentCommandEvent commandEvent, string key)
    {
        if (commandEvent.EventType is not (CommandEventType.ScheduleChanged or CommandEventType.Start) ||
            !commandEvent.Metadata.TryGetValue(key, out var value)) return null;
        if (!DateTimeOffset.TryParseExact(value, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var time))
            throw new InvalidDataException("持久化时间调整指令中的时间格式无效，未发送不完整时间计划。");
        return time.ToOffset(TimeSpan.FromHours(8));
    }

    private static CommandEventView ToView(PersistentCommandEvent commandEvent) =>
        new(
            commandEvent.EventId,
            commandEvent.ExamId,
            commandEvent.ParticipationId,
            commandEvent.EntryVersion,
            commandEvent.EventType,
            commandEvent.StateVersion,
            commandEvent.TargetDeviceId,
            commandEvent.TargetAuthorizationVersion,
            commandEvent.ResourceVersion,
            commandEvent.CreatedAt,
            commandEvent.Acknowledgement?.ProcessingResult is
                CommandProcessingResult.Applied or CommandProcessingResult.RejectedAsStale,
            commandEvent.DeliveryAttempts);
}
