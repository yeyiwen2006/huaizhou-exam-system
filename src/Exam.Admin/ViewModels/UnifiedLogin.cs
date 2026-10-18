using System.Net;
using System.Net.Http;
using Exam.Admin.Services;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ViewModels;

public sealed partial class ConnectionViewModel
{
    private string? _managementUsername;
    public bool IsAuthenticated => IsConnected || _managementUsername is not null;
    public bool IsLoginInProgress { get; private set; }
    public Func<string, string, string, Task<bool>>? LoginManagementRecoveryAsync { get; set; }
    public event Action? ManagementRecoveryEntered;
    internal void SetManagementIdentity(string? username)
    {
        _managementUsername = username;
        OnPropertyChanged(nameof(IsAuthenticated)); OnPropertyChanged(nameof(AccountTitle));
        LogoutCommand.RaiseCanExecuteChanged();
    }
    internal async Task CompleteBusinessRecoveryAsync(ManagementBusinessSession session, CancellationToken token)
    {
        await _api.LoginWithManagementSessionAsync(session, token);
        IsRegistering = false; IsConnected = true; ShowCurrentEndpoint();
    }
    private static bool BusinessUnavailable(Exception error) => error is HttpRequestException or TaskCanceledException ||
        error is AdminApiException api && api.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout ||
        error is AdminApiException { Code: "NOT_ACTIVE_SERVER" or "SERVICE_NOT_READY" or "REPLICA_UNAVAILABLE" };
}
