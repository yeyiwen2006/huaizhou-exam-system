using Exam.Admin.Infrastructure;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed partial class ServiceManagementViewModel
{
    public AsyncRelayCommand CancelHighAvailabilityCommand { get; }
    public string CancelHighAvailabilityText => _highAvailabilityOperation?.Kind == ManagementOperationKind.Takeover
        ? "取消接管" : "取消重建";

    private PendingManagementRequest? CancelableRequest() => _highAvailabilityOperation is { } operation &&
        operation.Kind is ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild &&
        operation.Phase is not (ManagementOperationPhase.Completed or ManagementOperationPhase.Failed or ManagementOperationPhase.Canceled) &&
        !operation.CancellationRequested && !(operation.Kind == ManagementOperationKind.Takeover &&
            _availability?.State is { } state && state.ActiveNodeId == operation.NodeId &&
            state.Nodes.Values.Any(node => node.NodeId != operation.NodeId && node.Role == Exam.Contracts.HighAvailability.DeploymentNodeRole.Isolated))
        ? _knownPendingRequests.Values.FirstOrDefault(request => request.RequestId == operation.RequestId)
            ?? (_pending?.RequestId == operation.RequestId ? _pending : null) : null;

    private bool CanCancelHighAvailability() => IsSignedIn && CancelableRequest() is not null;

    public async Task CancelHighAvailabilityAsync()
    {
        if (!IsSignedIn || CancelableRequest() is not { } request) return;
        var version = _managementSessionVersion;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(ManagementTimeouts.Read);
        try
        {
            var connection = NodeSession(request.Target).Connection ??
                await EnsureTargetSessionAsync(request.Target, deadline.Token, requestCredentials: false);
            if (connection is null || connection.Actor != request.Actor || version != _managementSessionVersion) return;
            var reply = await connection.CancelAsync(request.RequestId, deadline.Token);
            if (version != _managementSessionVersion) return;
            if (reply.Envelope.Result is not { } operation)
            { Message = "取消尚未确认，原操作继续保留。"; return; }
            ValidatePendingOperation(operation, request);
            RememberFailoverProgress(operation);
            if (operation.Phase is ManagementOperationPhase.Completed or ManagementOperationPhase.Failed or ManagementOperationPhase.Canceled)
            {
                _store.Clear(request);
                _knownPendingRequests.TryRemove(request.Target.Key, out _);
                if (_pending?.RequestId == request.RequestId) _pending = null;
                NotifyPending();
                PublishSettledRoleSnapshot();
                if (ReferenceEquals(connection, _connection))
                {
                    _generation++; _reads?.Cancel(); _reads = null; _readBusy = false;
                    _polling?.Cancel(); _availabilityPolling?.Cancel(); IsBusy = false;
                }
            }
            if (ReferenceEquals(connection, _connection)) Apply(reply, request, connection, _generation);
            StartPolling();
        }
        catch
        {
            if (version == _managementSessionVersion) Message = "取消结果尚未确认，后台将继续查询原请求。";
        }
    }
}
