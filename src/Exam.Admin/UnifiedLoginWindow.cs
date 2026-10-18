using System.ComponentModel;
using System.Windows.Threading;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin;

public partial class MainWindow
{
    private readonly CancellationTokenSource _loginRecoveryLifetime = new();
    private DispatcherTimer? _loginRecoveryTimer;
    private bool _resumingBusiness, _enteredThroughRecovery;
    private ManagementBusinessSession? _pendingBusinessSession;
    private (string Server, string Node, long Epoch)? _businessRecoveryTarget;

    private void ConfigureUnifiedLogin()
    {
        _viewModel.Connection.LoginManagementRecoveryAsync = Management.LoginForRecoveryAsync;
        _viewModel.Connection.ManagementRecoveryEntered += OnManagementRecoveryEntered;
        Management.PropertyChanged += OnManagementIdentityChanged;
        _loginRecoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _loginRecoveryTimer.Tick += OnLoginRecoveryTick;
        _loginRecoveryTimer.Start();
    }
    private void OnManagementRecoveryEntered()
    {
        _enteredThroughRecovery = true;
        OpenManagement();
        _ = ResumeBusinessFromManagementAsync();
    }
    private void OnManagementIdentityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Management.IsSignedIn) && Management.IsSignedIn)
            _viewModel.Connection.SetManagementIdentity(Management.Username);
    }
    private void OnLoginRecoveryTick(object? sender, EventArgs e)
    {
        if (!_viewModel.Connection.IsConnected && Management.IsSignedIn && !_viewModel.Connection.IsLoginInProgress)
            _ = ResumeBusinessFromManagementAsync();
    }
    private async Task ResumeBusinessFromManagementAsync()
    {
        if (_resumingBusiness || !Management.IsSignedIn || _loginRecoveryLifetime.IsCancellationRequested) return;
        _resumingBusiness = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_loginRecoveryLifetime.Token);
        deadline.CancelAfter(ManagementTimeouts.Read);
        try
        {
            if (_businessRecoveryTarget is { } target)
            {
                var current = _viewModel.CaptureBusinessSession();
                if (current is not null && current.ServerId == target.Server && current.NodeId == target.Node && current.ServerEpoch >= target.Epoch) return;
                if (current is not null && await _viewModel.ReconnectToActiveNodeAsync(target.Server, target.Node, target.Epoch, deadline.Token)) return;
                if (_pendingBusinessSession is { } pending && (pending.ServerId != target.Server || pending.ServerEpoch < target.Epoch ||
                    pending.ServerEpoch == target.Epoch && pending.NodeId != target.Node)) _pendingBusinessSession = null;
            }
            _pendingBusinessSession ??= await Management.CreateBusinessSessionAsync(deadline.Token);
            if (_pendingBusinessSession is null) return;
            await _viewModel.Connection.CompleteBusinessRecoveryAsync(_pendingBusinessSession, deadline.Token);
            _pendingBusinessSession = null;
            Management.ClearRecoveryLoginNotice();
            if (_enteredThroughRecovery)
            {
                _enteredThroughRecovery = false;
                _businessPage = ViewModels.MainViewModel.HomePage;
                ReturnToBusiness();
            }
        }
        catch (Exception) { /* Keep each management session and the exact pending operations. */ }
        finally { _resumingBusiness = false; }
    }
    private void ShutdownUnifiedLogin()
    {
        _loginRecoveryTimer?.Stop();
        _loginRecoveryLifetime.Cancel();
        _pendingBusinessSession = null;
        _viewModel.Connection.ManagementRecoveryEntered -= OnManagementRecoveryEntered;
        Management.PropertyChanged -= OnManagementIdentityChanged;
        _viewModel.Connection.LoginManagementRecoveryAsync = null;
    }
}
