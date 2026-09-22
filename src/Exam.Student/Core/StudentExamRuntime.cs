using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Exam.Contracts.Commands;
using Exam.Contracts.Participation;

namespace Exam.Student.Core;

/// <summary>一场考试的包与指令连接，在等待页转到答题页时继续保留。</summary>
public sealed class StudentExamRuntime(ExamPackageManager manager, DeviceRecord device) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private IExamCommandFeed? _feed;
    private ParticipationResult? _connectedParticipation;
    private ServerNodeEnvelope? _connectedServer;
    private bool _disposed;
    private string? _handlingError;

    public ExamPackageManager Manager { get; } = manager;
    public string? ConnectionError => (_feed as SignalRExamCommandFeed)?.LastError ?? _handlingError;
    public bool NotificationsStarted => _feed is not null;
    public event EventHandler? StateChanged;

    public async Task StartNotificationsAsync(ParticipationResult participation, ServerNodeEnvelope server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_feed is SignalRExamCommandFeed { IsConnected: true } &&
            _connectedServer == server && _connectedParticipation is { } previous &&
            previous.ExamId == participation.ExamId && previous.CandidateId == participation.CandidateId &&
            previous.ParticipationId == participation.ParticipationId && previous.EntryVersion == participation.EntryVersion &&
            previous.AuthorizedDeviceId == participation.AuthorizedDeviceId && previous.AuthorizationVersion == participation.AuthorizationVersion)
        {
            return;
        }

        if (_feed is not null)
        {
            await _feed.DisposeAsync();
            _feed = null;
        }

        var feed = new SignalRExamCommandFeed(device, server)
        {
            AuthorizationLost = InvalidateAuthorizationAsync,
        };
        try
        {
            await feed.StartAsync(participation, ReceiveCommandAsync, _lifetime.Token);
            _feed = feed;
            _connectedParticipation = participation;
            _connectedServer = server;
            _handlingError = null;
        }
        catch
        {
            await feed.DisposeAsync();
            _handlingError = "指令连接尚未建立，请重试连接；原参试登记保持不变。";
            throw;
        }
    }

    private Task ReceiveCommandAsync(CommandDelivery delivery, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("学生端界面尚未启动。");
        return dispatcher.CheckAccess()
            ? ApplyCommandAsync(delivery, ct)
            : dispatcher.InvokeAsync(() => ApplyCommandAsync(delivery, ct), DispatcherPriority.Normal, ct).Task.Unwrap();
    }

    private Task InvalidateAuthorizationAsync(CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("学生端界面尚未启动。");
        // 连接自己的取消信号可能正是由撤销触发，不能因此跳过本地停用。
        return dispatcher.CheckAccess()
            ? InvalidateAuthorizationOnUiAsync()
            : dispatcher.InvokeAsync(InvalidateAuthorizationOnUiAsync, DispatcherPriority.Send).Task.Unwrap();
    }

    private async Task InvalidateAuthorizationOnUiAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await Manager.InvalidateAuthorizationAsync(_lifetime.Token);
            _handlingError = "当前设备的答题授权已失效，已停止访问试题。请联系考务人员核对授权。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is IOException or CryptographicException or UnauthorizedAccessException)
        {
            _handlingError = "当前设备授权已失效，已停止访问试题；本机停用记录保存失败，请联系考务人员恢复。";
        }

        // 此回调仍属于指令连接，不在其中 Dispose/Stop 该连接，避免等待自身结束。
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyCommandAsync(CommandDelivery delivery, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await Manager.HandleCommandAsync(delivery, ct);
            _handlingError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or TimeoutException
            or OperationCanceledException or CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            _handlingError = "服务器指令尚未处理完成，将继续使用原事件重试。请保持客户端运行。";
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        try
        {
            if (_feed is not null)
            {
                await _feed.DisposeAsync();
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        {
            // 服务端已确认退出后的本地连接清理不应重新阻止退出；持久登记由管理器处理。
        }
        finally
        {
            _feed = null;
            _connectedParticipation = null;
            _connectedServer = null;
            Manager.Dispose();
            _lifetime.Dispose();
        }
    }
}
