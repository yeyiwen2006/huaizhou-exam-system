using System.Net;
using System.Net.Http;
using Exam.Contracts.Administration;

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
        if (!StringComparer.Ordinal.Equals(result.ExamId, examId) || result.Participants is null ||
            result.Uncollected is null || result.Versions is null || result.ActualCount < 0 ||
            result.CollectedCount < 0 || result.CollectedCount > result.ActualCount)
            throw new AdminApiException("INVALID_RESPONSE", "监控快照不属于当前考试或人数无效，未应用该结果。", HttpStatusCode.BadGateway);
        return result;
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
