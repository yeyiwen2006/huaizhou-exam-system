using System.Security.Cryptography;
using Exam.Contracts.Activation;
using Exam.Contracts.Common;

namespace Exam.Student.Core;

/// <summary>身份申请先保存请求标识；响应未知时重试原请求，取得申请标识后只查询状态。</summary>
public sealed class ActivationWorkflow(DeviceStore store, IStudentApi api)
{
    public async Task<DeviceRecord> RequestOrPollAsync(string host, int port, CancellationToken ct = default)
    {
        var record = await Task.Run(() => store.Load(host, port), ct);
        if (record is null)
        {
            record = new DeviceRecord($"dev-{Guid.NewGuid():N}", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                null, DeviceStore.NormalizeHost(host), port, default, SubmitRequestId: RequestIds.New());
            await SaveAsync(record, ct);
        }

        api.ConfigureConnection(record);
        if (record.Status == ActivationStatus.Active)
        {
            return record;
        }

        if (record.Status == ActivationStatus.Revoked)
        {
            record = await BeginOperationAsync(record, "reactivate", ct);
            var response = await api.ReactivateAsync(new ReactivateDeviceRequest(record.PendingOperationRequestId!, record.DeviceId, record.Credential), ct);
            return await SaveResultAsync(record, response, ActivationStatus.Active, ct);
        }

        if (record.Status == ActivationStatus.Rejected)
        {
            throw new StudentIdentityException("激活申请已被拒绝，请联系考务人员处理。");
        }

        ApiEnvelope<ActivationRequestResult> envelope;
        if (record.ActivationRequestId is { } activationRequestId)
        {
            envelope = await api.GetActivationStatusAsync(new GetActivationStatusRequest(activationRequestId, record.DeviceId, record.Credential), ct);
        }
        else
        {
            if (record.SubmitRequestId is null)
            {
                record = record with { SubmitRequestId = RequestIds.New() };
                await SaveAsync(record, ct);
            }

            envelope = await api.SubmitActivationAsync(new SubmitActivationRequest(record.SubmitRequestId, record.DeviceId, record.Credential), ct);
        }

        var result = Result(envelope, record);
        if (result.DeviceId != record.DeviceId || string.IsNullOrWhiteSpace(result.ActivationRequestId) ||
            (record.ActivationRequestId is not null && result.ActivationRequestId != record.ActivationRequestId))
        {
            throw new StudentIdentityException("服务器返回的设备登记不匹配，请联系考务人员。");
        }

        record = record with
        {
            ServerId = envelope.ServerId,
            ServerEpoch = envelope.ServerEpoch,
            Status = result.Status,
            ClientNumber = result.ClientNumber ?? record.ClientNumber,
            CredentialVersion = result.CredentialVersion,
            ActivationRequestId = result.ActivationRequestId,
            ActivatedAt = result.Status == ActivationStatus.Active ? result.RequestedAt : record.ActivatedAt,
            Topology = result.Status == ActivationStatus.Active
                ? TrustedServerTopology.FromActivation(result.Topology, record, envelope.ServerId) : record.Topology,
            ActiveNodeId = result.Status == ActivationStatus.Active ? envelope.NodeId : record.ActiveNodeId,
        };
        await SaveAsync(record, ct);
        api.ConfigureConnection(record);
        return record;
    }

    public async Task<DeviceRecord> CancelAsync(DeviceRecord record, CancellationToken ct = default)
    {
        if (record.Status != ActivationStatus.Active)
        {
            throw new StudentIdentityException("设备当前未激活。");
        }

        record = await BeginOperationAsync(record, "cancel", ct);
        api.ConfigureConnection(record);
        var response = await api.CancelActivationAsync(new CancelOwnActivationRequest(record.PendingOperationRequestId!, record.DeviceId, record.Credential), ct);
        return await SaveResultAsync(record, response, ActivationStatus.Revoked, ct);
    }

    private async Task<DeviceRecord> BeginOperationAsync(DeviceRecord record, string operation, CancellationToken ct)
    {
        var persisted = await Task.Run(() => store.Load(record.Host, record.Port), ct)
            ?? throw new StudentIdentityException("未找到原设备身份，请联系考务人员恢复。");
        if (persisted.DeviceId != record.DeviceId || persisted.Credential != record.Credential)
        {
            throw new StudentIdentityException("设备身份与本机登记不一致，已停止操作。");
        }

        record = persisted;
        if (record.PendingOperation is not null && record.PendingOperation != operation)
        {
            throw new StudentIdentityException("上一次身份操作尚未完成，请先重试该操作。");
        }

        record = record with { PendingOperation = operation, PendingOperationRequestId = record.PendingOperationRequestId ?? RequestIds.New() };
        await SaveAsync(record, ct);
        return record;
    }

    private async Task<DeviceRecord> SaveResultAsync(DeviceRecord record, ApiEnvelope<ActivationResult> envelope,
        ActivationStatus expectedStatus, CancellationToken ct)
    {
        var result = Result(envelope, record);
        if (result.DeviceId != record.DeviceId || result.Status != expectedStatus ||
            (record.ClientNumber is not null && record.ClientNumber != result.ClientNumber))
        {
            throw new StudentIdentityException("服务器返回的设备状态不匹配，请联系考务人员。");
        }

        record = record with
        {
            ServerId = envelope.ServerId,
            ServerEpoch = envelope.ServerEpoch,
            ClientNumber = result.ClientNumber,
            Status = result.Status,
            CredentialVersion = result.CredentialVersion,
            PendingOperation = null,
            PendingOperationRequestId = null,
            Topology = TrustedServerTopology.FromActivation(result.Topology, record, envelope.ServerId),
            ActiveNodeId = envelope.NodeId,
        };
        await SaveAsync(record, ct);
        api.ConfigureConnection(record);
        return record;
    }

    private static T Result<T>(ApiEnvelope<T> envelope, DeviceRecord record)
    {
        if (!envelope.IsSuccess || envelope.Result is null)
        {
            throw new StudentIdentityException(envelope.Error?.Message ?? "服务器未确认身份操作，请重试。");
        }

        if (string.IsNullOrWhiteSpace(envelope.ServerId) || envelope.ServerEpoch < 1 ||
            (record.ServerId is not null && record.ServerId != envelope.ServerId) || envelope.ServerEpoch < record.ServerEpoch)
        {
            throw new StudentIdentityException("服务器身份或服务代次不匹配，已停止身份操作。");
        }

        return envelope.Result;
    }

    private Task SaveAsync(DeviceRecord record, CancellationToken ct) => Task.Run(() => store.Save(record), ct);
}

public sealed class StudentIdentityException(string message) : InvalidOperationException(message);
