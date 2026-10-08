using System.Security.Cryptography;
using Exam.Contracts.Activation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;

namespace Exam.Student.Core;

/// <summary>每次由用户指定地址连接；只复用受保护的设备身份，不自动登录。</summary>
public sealed class StudentLoginWorkflow(DeviceStore store, IStudentApi api)
{
    public async Task<(DeviceRecord Device, StudentSessionResult Session)> LoginAsync(
        string host, int port, string name, string classText, string admissionNo, CancellationToken ct = default)
    {
        host = DeviceStore.NormalizeHost(host);
        if (port is < 1 or > 65535) throw new StudentIdentityException("端口号必须在 1 到 65535 之间。");
        var record = await Task.Run(() => store.Load(host, port), ct);
        if (record is null)
        {
            record = new DeviceRecord($"dev-{Guid.NewGuid():N}", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                null, host, port, default);
            // Persist before the first request so a lost response cannot create another device.
            await Task.Run(() => store.Save(record), ct);
        }
        api.ConfigureLoginConnection(record, host, port);
        var envelope = await api.ConnectAndLoginAsync(new LoginStudentRequest(
            RequestIds.New(), record.DeviceId, record.Credential, name, classText, admissionNo), ct);
        if (!envelope.IsSuccess || envelope.Result is null)
            throw new StudentLoginException(envelope.Error ?? new ApiError("LOGIN_FAILED", "登录未完成，请重试。"));
        var result = envelope.Result;
        var number = result.Device.ClientNumber;
        if (result.Device.DeviceId != record.DeviceId || result.Session.DeviceId != record.DeviceId ||
            result.Session.Candidate.Name != name || result.Session.Candidate.ClassText != classText ||
            result.Session.Candidate.AdmissionNo != admissionNo || string.IsNullOrWhiteSpace(result.Session.SessionId) ||
            result.Device.Status != ActivationStatus.Active || number.Length != 12 || !number.All(char.IsAsciiDigit) ||
            number[8..] == "0000" || number[..8] != result.Device.ActivationDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) ||
            string.IsNullOrWhiteSpace(envelope.ServerId) || envelope.ServerEpoch < 1 ||
            (record.ServerId is not null && record.ServerId != envelope.ServerId) || envelope.ServerEpoch < record.ServerEpoch)
            throw new StudentIdentityException("服务器返回的登录信息不匹配，请联系考务人员。");

        // Transport may have persisted a newer trusted endpoint while reconnecting.
        record = await Task.Run(() => store.Load(record.Host, record.Port), ct) ?? record;
        record = record with
        {
            ServerId = envelope.ServerId, ServerEpoch = envelope.ServerEpoch,
            ClientNumber = number, Status = ActivationStatus.Active, ActivatedAt = result.Session.AuthenticatedAt,
            CredentialVersion = result.Device.CredentialVersion, ActiveNodeId = envelope.NodeId,
            Topology = TrustedServerTopology.FromActivation(result.Device.Topology, record, envelope.ServerId),
            PendingOperation = null, PendingOperationRequestId = null
        };
        await Task.Run(() => store.Save(record), ct);
        api.ConfigureConnection(record);
        return (record, result.Session);
    }
}

public sealed class StudentLoginException(ApiError error) : InvalidOperationException(error.Message)
{
    public ApiError Error { get; } = error;
}
