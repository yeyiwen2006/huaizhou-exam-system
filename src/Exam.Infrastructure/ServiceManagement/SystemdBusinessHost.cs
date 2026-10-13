using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Controls one deployment-selected systemd unit; no command or unit comes from API requests.</summary>
public sealed class SystemdBusinessHost : IManagedBusinessHost
{
    private readonly string _unit;
    private readonly IExternalProcessRunner _runner;
    private readonly string _systemctl;
    private readonly TimeSpan _timeout;

    public SystemdBusinessHost(string serviceName, IExternalProcessRunner? runner = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Length > 100 ||
            !char.IsAsciiLetterOrDigit(serviceName[0]) ||
            serviceName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
            throw new ArgumentException("受控 systemd 服务名称无效。", nameof(serviceName));
        _unit = serviceName.EndsWith(".service", StringComparison.Ordinal) ? serviceName : serviceName + ".service";
        _runner = runner ?? new ExternalProcessRunner();
        _systemctl = File.Exists("/usr/bin/systemctl") ? "/usr/bin/systemctl" : "/bin/systemctl";
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public BusinessServiceStatus Status
    {
        get
        {
            try { return ReadStateAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            { return BusinessServiceStatus.Faulted; }
        }
    }

    public Task StartAsync(CancellationToken token) => ChangeAsync("start", BusinessServiceStatus.Running, token);
    public Task StopAsync(CancellationToken token) => ChangeAsync("stop", BusinessServiceStatus.Stopped, token);

    private async Task ChangeAsync(string action, BusinessServiceStatus expected, CancellationToken token)
    {
        if (await ReadStateAsync(token).ConfigureAwait(false) == expected) return;
        var result = await _runner.RunAsync(_systemctl, [action, "--no-block", _unit],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("systemd 服务操作失败，请核对服务名称和服务器账户权限。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        try
        {
            while (true)
            {
                var state = await ReadStateAsync(deadline.Token).ConfigureAwait(false);
                if (state == expected) return;
                if (state == BusinessServiceStatus.Faulted) throw new InvalidOperationException("systemd 服务进入失败状态。");
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("等待 systemd 服务状态超时，请核对服务器上的实际状态。"); }
    }

    private async Task<BusinessServiceStatus> ReadStateAsync(CancellationToken token)
    {
        var result = await _runner.RunAsync(_systemctl, ["show", "--property=LoadState,ActiveState", _unit],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (result.ExitCode != 0) return BusinessServiceStatus.Faulted;
        var values = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (values.GetValueOrDefault("LoadState") != "loaded") return BusinessServiceStatus.Faulted;
        return values.GetValueOrDefault("ActiveState") switch
        {
            "active" => BusinessServiceStatus.Running,
            "inactive" => BusinessServiceStatus.Stopped,
            "activating" => BusinessServiceStatus.Starting,
            "deactivating" => BusinessServiceStatus.Stopping,
            _ => BusinessServiceStatus.Faulted
        };
    }
}
