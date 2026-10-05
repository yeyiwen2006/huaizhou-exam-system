using System.Net;
using System.Net.Http;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient
{
    public async Task<CandidateRosterSnapshot> GetRosterAsync(string examId, CancellationToken ct = default)
    {
        var result = await SendAsync<CandidateRosterSnapshot>(HttpMethod.Get, RosterPath(examId), null, ct);
        RequireRosterIdentity(examId, result);
        return result;
    }

    public async Task<RosterImportResult> ImportRosterAsync(string examId, long expectedVersion,
        IReadOnlyList<RosterImportRow> rows, CancellationToken ct = default)
    {
        var result = await SendAsync<RosterImportResult>(HttpMethod.Post, RosterPath(examId) + "/import",
            new ImportCandidateRosterRequest(Guid.NewGuid().ToString("N"), expectedVersion, rows), ct);
        RequireRosterIdentity(examId, result.Snapshot);
        return result;
    }

    public async Task<CandidateRosterSnapshot> UpdateRosterEntryAsync(string examId, string candidateId,
        long expectedVersion, RosterImportRow entry, CancellationToken ct = default)
    {
        var result = await SendAsync<CandidateRosterSnapshot>(HttpMethod.Put,
            RosterPath(examId) + "/" + Uri.EscapeDataString(candidateId),
            new UpdateCandidateRosterRequest(Guid.NewGuid().ToString("N"), expectedVersion, entry), ct);
        RequireRosterIdentity(examId, result);
        return result;
    }

    public async Task<CandidateRosterSnapshot> DeleteRosterEntryAsync(string examId, string candidateId,
        long expectedVersion, CancellationToken ct = default)
    {
        var result = await SendAsync<CandidateRosterSnapshot>(HttpMethod.Delete,
            RosterPath(examId) + "/" + Uri.EscapeDataString(candidateId),
            new DeleteCandidateRosterRequest(Guid.NewGuid().ToString("N"), expectedVersion), ct);
        RequireRosterIdentity(examId, result);
        return result;
    }

    public Task<IReadOnlyList<RoomGroup>> ListRoomGroupsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<RoomGroup>>(HttpMethod.Get, "api/v1/rooms", null, ct);

    public Task<RoomGroup> CreateRoomGroupAsync(string name, CancellationToken ct = default) =>
        SendAsync<RoomGroup>(HttpMethod.Post, "api/v1/rooms",
            new CreateRoomGroupRequest(Guid.NewGuid().ToString("N"), name), ct);

    public Task<RoomGroup> RenameRoomGroupAsync(string groupId, long expectedVersion, string name,
        CancellationToken ct = default) => SendAsync<RoomGroup>(HttpMethod.Put,
            "api/v1/rooms/" + Uri.EscapeDataString(groupId),
            new RenameRoomGroupRequest(Guid.NewGuid().ToString("N"), expectedVersion, name), ct);

    public Task<DeletedRoomGroup> DeleteRoomGroupAsync(string groupId, long expectedVersion,
        CancellationToken ct = default) => SendAsync<DeletedRoomGroup>(HttpMethod.Delete,
            "api/v1/rooms/" + Uri.EscapeDataString(groupId),
            new DeleteRoomGroupRequest(Guid.NewGuid().ToString("N"), expectedVersion), ct);

    public Task<MoveDevicesResult> MoveDevicesAsync(string? groupId, IReadOnlyList<DeviceMoveTarget> devices,
        string reason, CancellationToken ct = default) => SendAsync<MoveDevicesResult>(HttpMethod.Post,
            "api/v1/rooms/move-devices", new MoveDevicesRequest(Guid.NewGuid().ToString("N"), groupId, devices, reason), ct);

    public Task<DeviceDirectoryPage> GetDevicesPageAsync(string? groupId = null, string? query = null,
        int offset = 0, CancellationToken ct = default) => SendAsync<DeviceDirectoryPage>(HttpMethod.Get,
            $"api/v1/devices?limit=2000&offset={offset}" +
            (groupId is null ? "" : "&roomGroupId=" + Uri.EscapeDataString(groupId)) +
            (string.IsNullOrWhiteSpace(query) ? "" : "&query=" + Uri.EscapeDataString(query.Trim())), null, ct);

    public Task<IReadOnlyList<DeviceRoomHistory>> GetDeviceRoomHistoryAsync(string clientNumber,
        CancellationToken ct = default) => SendAsync<IReadOnlyList<DeviceRoomHistory>>(HttpMethod.Get,
            "api/v1/devices/" + Uri.EscapeDataString(clientNumber) + "/room-history", null, ct);

    public async Task<MonitoringSnapshot> GetMonitoringSnapshotAsync(string examId, CancellationToken ct = default)
    {
        var result = await SendAsync<MonitoringSnapshot>(HttpMethod.Get,
            "api/v1/exams/" + Uri.EscapeDataString(examId) + "/monitoring", null, ct);
        RequireMonitoringSnapshot(examId, result);
        return result;
    }

    /// <summary>
    /// 返回前校验完毕：本客户端按考试**整取**快照（从不传 roomGroupId/query），因此人数、未回收名单与参试列表互相自洽；
    /// 服务端筛选只过滤参试列表、不调整计数与未回收名单，所以一旦将来启用筛选，这些自洽条件必须改为按返回列表计算；
    /// 任一字段越界、枚举未知或身份不符都整体拒绝，不把半份状态交给界面。
    /// </summary>
    internal static void RequireMonitoringSnapshot(string examId, MonitoringSnapshot? result)
    {
        const string message = "监控快照不属于当前考试或记录无效，未应用该结果。";
        if (result is null || !StringComparer.Ordinal.Equals(result.ExamId, examId))
            throw Invalid();
        var versions = result.Versions as Exam.Contracts.Versioning.VersionVector;
        if (versions is null || versions.PaperVersion < 0 || versions.GradingVersion < 0 || versions.ScheduleVersion < 0 ||
            result.Participants is null || result.Uncollected is null || result.AsOf == default ||
            !Enum.IsDefined(result.Lifecycle) || result.ActualRosterVersion < 0 ||
            result.ActualCount < 0 || result.CollectedCount < 0 || result.CollectedCount > result.ActualCount ||
            result.Participants.Count != result.ActualCount ||
            result.Uncollected.Count != result.ActualCount - result.CollectedCount)
            throw Invalid();

        var byId = new Dictionary<string, MonitoringParticipant>(StringComparer.Ordinal);
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in result.Participants)
        {
            // 同一名考生在一场考试里只能有一条权威参试记录。
            if (!IsWellFormed(participant) || !byId.TryAdd(participant.ParticipationId, participant) ||
                !candidateIds.Add(participant.CandidateId))
                throw Invalid();
        }
        // 已回收份数必须等于真正带回执的参试记录数，否则会出现“未回收名单为空却显示已回收 3 份”的半份状态。
        // 放在逐个校验之后：列表里可能含 null 元素，必须先由 IsWellFormed 拒绝。
        if (result.CollectedCount != result.Participants.Count(item => item.ReceiptId is not null))
            throw Invalid();
        var uncollected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in result.Uncollected)
        {
            if (!IsWellFormed(participant) || !uncollected.Add(participant.ParticipationId) ||
                !byId.TryGetValue(participant.ParticipationId, out var matching) ||
                matching.ReceiptId is not null || participant.ReceiptId is not null)
                throw Invalid();
        }
        // 已回收的参试记录必须带持久化回执标识——未持久保存不得显示为已回收。
        if (result.Participants.Any(participant => participant.CollectionState switch
            {
                "已回收" => string.IsNullOrWhiteSpace(participant.ReceiptId) || participant.ReceivedAt is null,
                "未回收" or "回收中" => participant.ReceiptId is not null,
                _ => true
            }))
            throw Invalid();
        return;

        AdminApiException Invalid() => new("INVALID_RESPONSE", message, HttpStatusCode.BadGateway);
    }

    private static bool IsWellFormed(MonitoringParticipant? participant)
    {
        if (participant is null ||
            string.IsNullOrWhiteSpace(participant.ParticipationId) || string.IsNullOrWhiteSpace(participant.CandidateId) ||
            string.IsNullOrWhiteSpace(participant.Name) || string.IsNullOrWhiteSpace(participant.ClassText) ||
            string.IsNullOrWhiteSpace(participant.AdmissionNo) || string.IsNullOrWhiteSpace(participant.ClientNumber) ||
            string.IsNullOrWhiteSpace(participant.AuthorizedDeviceId) ||
            !Enum.IsDefined(participant.BusinessState) || participant.BusinessState == ParticipationBusinessState.NotParticipating ||
            !Enum.IsDefined(participant.ConnectionState) ||
            participant.EntryVersion < 1 || participant.AuthorizationVersion < 1 || participant.ParticipationVersion < 1 ||
            participant.AnswerVersion < 0 || participant.InstalledPaperVersion < 0 || participant.TargetPaperVersion < 0 ||
            participant.FocusLossCount < 0 || participant.FocusLossMilliseconds < 0 ||
            participant.Answers is null || participant.Commands is null || participant.ConflictDevices is null ||
            participant.RevisionStatus is { } revision && !Enum.IsDefined(revision.PaperUpdateState))
            return false;
        if (participant.Page.Length > 64 || participant.DownloadState.Length > 64 ||
            participant.CollectionState is not ("未回收" or "回收中" or "已回收"))
            return false;
        if (participant.Answers.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                pair.Value.OptionIds is null || !Enum.IsDefined(pair.Value.Kind) ||
                pair.Value.Kind != AnswerKind.Selected && pair.Value.OptionIds.Count != 0 ||
                pair.Value.OptionIds.Any(string.IsNullOrWhiteSpace)))
            return false;
        if (participant.Commands.Any(command => command is null || string.IsNullOrWhiteSpace(command.EventId) ||
                !Enum.IsDefined(command.EventType) || command.StateVersion < 1 || command.CreatedAt == default ||
                string.IsNullOrWhiteSpace(command.Confirmation)))
            return false;
        return participant.ConflictDevices.All(conflict => conflict is not null &&
            !string.IsNullOrWhiteSpace(conflict.DeviceId) && !string.IsNullOrWhiteSpace(conflict.ClientNumber) &&
            !string.IsNullOrWhiteSpace(conflict.RoomGroupName) && Enum.IsDefined(conflict.ConnectionState) &&
            conflict.AnswerVersion >= 0);
    }

    private static string RosterPath(string examId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        return "api/v1/exams/" + Uri.EscapeDataString(examId) + "/roster";
    }

    private static void RequireRosterIdentity(string examId, CandidateRosterSnapshot result)
    {
        if (result is null || !StringComparer.Ordinal.Equals(result.ExamId, examId) || result.Version < 0 ||
            result.Entries is null || result.Entries.Any(x => x is null || string.IsNullOrWhiteSpace(x.CandidateId)) ||
            result.Entries.Select(x => x.CandidateId).Distinct(StringComparer.Ordinal).Count() != result.Entries.Count ||
            result.Entries.Select(x => x.AdmissionNo).Distinct(StringComparer.Ordinal).Count() != result.Entries.Count)
            throw new AdminApiException("INVALID_RESPONSE", "名册响应与当前考试不一致或记录无效，未应用该结果。", HttpStatusCode.BadGateway);
    }
}
