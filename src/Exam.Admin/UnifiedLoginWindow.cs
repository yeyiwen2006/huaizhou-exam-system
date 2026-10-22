using System.ComponentModel;
using System.Windows.Threading;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin;

public partial class MainWindow
{
    private readonly CancellationTokenSource _loginRecoveryLifetime = new();
    private DispatcherTimer? _loginRecoveryTimer;
    private bool _resumingBusiness, _enteredThroughRecovery;
    private CancellationTokenSource? _loginRecoveryAttempt;
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
        if (e.PropertyName != nameof(Management.IsSignedIn)) return;
        if (!Management.IsSignedIn)
        {
            _loginRecoveryAttempt?.Cancel();
            _pendingBusinessSession = null;
            _businessRecoveryTarget = null;
            _enteredThroughRecovery = false;
        }
        _viewModel.Connection.SetManagementIdentity(Management.IsSignedIn ? Management.Username : null);
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
        _loginRecoveryAttempt = deadline;
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
            var pendingSession = _pendingBusinessSession ?? await Management.CreateBusinessSessionAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (pendingSession is null) return;
            if (_businessRecoveryTarget is { } currentTarget && (pendingSession.ServerId != currentTarget.Server ||
                pendingSession.ServerEpoch < currentTarget.Epoch ||
                pendingSession.ServerEpoch == currentTarget.Epoch && pendingSession.NodeId != currentTarget.Node))
            { _pendingBusinessSession = null; return; }
            _pendingBusinessSession = pendingSession;
            await _viewModel.Connection.CompleteBusinessRecoveryAsync(pendingSession, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
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
        finally { _loginRecoveryAttempt = null; _resumingBusiness = false; }
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
