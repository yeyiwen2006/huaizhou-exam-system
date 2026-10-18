using System.Globalization;
using Exam.Admin.Services;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed partial class ServiceManagementViewModel
{
    private CancellationTokenSource? _backupMonitoring;
    private string _automaticBackupText = "自动备份与磁盘情况尚未核实。";
    private string _lastBackupSummary = string.Empty;
    private string _lastBackupWarnings = string.Empty;
    private string _backupTargetKey = string.Empty;
    private bool _showAutomaticBackupWarning;
    private bool _automaticMonitoringEnabled;
    public string AutomaticBackupText { get => _automaticBackupText; private set => SetProperty(ref _automaticBackupText, value); }
    public bool ShowAutomaticBackupWarning { get => _showAutomaticBackupWarning; private set => SetProperty(ref _showAutomaticBackupWarning, value); }

    // A read-only observation independent of which exam tab or management page is visible.
    // It never uses the management write client and never changes the business login state.
    private void RestartBackupMonitoring()
    {
        _backupMonitoring?.Cancel();
        if (_disposed || _businessSession is not { } session)
        {
            ShowAutomaticBackupWarning = false;
            _lastBackupSummary = string.Empty;
            _lastBackupWarnings = string.Empty;
            _backupTargetKey = string.Empty;
            AutomaticBackupText = "自动备份与磁盘情况尚未核实。";
            return;
        }
        if (!_automaticMonitoringEnabled) return;
        if (!HasConnectionConfiguration || session.ServerId != ServerId.Trim())
        { MarkBackupUnknown("自动备份与磁盘告警尚未接通，请核对服务器管理连接配置。"); return; }
        var polling = _backupMonitoring = new CancellationTokenSource();
        var target = _connection?.Target ?? CurrentTarget();
        if (_backupTargetKey != target.Key)
        {
            _lastBackupSummary = string.Empty;
            _lastBackupWarnings = string.Empty;
            _backupTargetKey = target.Key;
        }
        _ = PollBackupStatusAsync(session, target, polling);
    }

    private async Task PollBackupStatusAsync(AdminSessionSnapshot session, ManagementTarget target, CancellationTokenSource polling)
    {
        bool CurrentObservation() => !_disposed && !polling.IsCancellationRequested &&
            _businessSession?.Matches(session) == true &&
            SameEndpoint(_connection?.Target ?? CurrentTarget(), target);
        try
        {
            // Let a connection-configuration edit settle before opening a read-only connection.
            await Task.Delay(TimeSpan.FromMilliseconds(250), _time, polling.Token);
            while (CurrentObservation())
            {
                try
                {
                    using var connection = _factory(target);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(polling.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(25));
                    await connection.UseBusinessSessionAsync(session, deadline.Token);
                    var reply = await connection.StatusAsync(deadline.Token);
                    if (!CurrentObservation()) return;
                    if (reply.IsUnauthorized || reply.Envelope.Error is not null || reply.Envelope.Result is null ||
                        reply.Envelope.ServerId != target.ServerId || reply.Envelope.NodeId != target.NodeId)
                        MarkBackupUnknown("自动备份状态读取失败，请检查管理连接、权限与服务运行状态；上次空间数值不代表当前情况。");
                    else ApplyAutomaticBackupStatus(reply.Envelope.Result.AutomaticBackup);
                }
                catch (OperationCanceledException) when (polling.IsCancellationRequested) { break; }
                catch
                {
                    if (CurrentObservation()) MarkBackupUnknown("自动备份状态暂时无法读取，请核对管理服务；原有告警不会因失联而解除。");
                }
                await Task.Delay(TimeSpan.FromMinutes(1), _time, polling.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_backupMonitoring, polling)) _backupMonitoring = null;
            polling.Dispose();
        }
    }

    public void ApplyAutomaticBackupStatus(BackupMaintenanceStatus? value)
    {
        if (value is null)
        { MarkBackupUnknown("当前节点暂未返回自动备份与磁盘状态，请刷新或核对管理服务。"); return; }
        if (value.SchemaVersion != 1 || value.Warnings is null || value.Warnings.Count > 16 ||
            value.Warnings.Any(w => w is null || w.Length > 1000) || !Enum.IsDefined(value.BackupPhase) || !Enum.IsDefined(value.CleanupPhase))
        { MarkBackupUnknown("自动备份状态无效，磁盘和恢复保护情况尚未核实。"); return; }
        if (!value.Enabled)
        {
            _lastBackupSummary = AutomaticBackupText = "此节点未启用自动备份维护。";
            _lastBackupWarnings = string.Empty;
            ShowAutomaticBackupWarning = false; return;
        }
        var warnings = value.Warnings.ToList();
        var now = _time.GetUtcNow();
        if (now - value.ObservedAt > TimeSpan.FromMinutes(15) || value.ObservedAt > now.AddMinutes(5))
            warnings.Add("维护状态已经过期或时间无效，请检查管理宿主；当前磁盘空间尚未核实。");
        if (value.AvailableBytes is { } free && free < value.MinimumAvailableBytes)
            warnings.Add("可用空间低于安全阈值，请立即处理；暂停新备份不会停止 WAL 增长。");
        if (value.BackupPhase == BackupMaintenancePhase.Failed || value.CleanupPhase == BackupMaintenancePhase.Failed)
            warnings.Add("备份或清理任务未完成，请处理告警并核对最近成功备份。");
        string When(DateTimeOffset? date) => date?.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "尚无";
        _lastBackupSummary = $"自动备份：{MaintenancePhase(value.BackupPhase)}；清理：{MaintenancePhase(value.CleanupPhase)}；最近成功：{When(value.LastSuccessfulBackupAt)}。\n" +
            $"可用空间 {Size(value.AvailableBytes)}（阈值 {Size(value.MinimumAvailableBytes)}）；备份 {Size(value.BackupBytes)}；恢复归档 {Size(value.ArchiveBytes)}；pg_wal {Size(value.WalBytes)}；复制保留 {Size(value.ReplicationRetainedBytes)}。";
        _lastBackupSummary += "\n观察时间：" + When(value.ObservedAt) + "。";
        _lastBackupWarnings = warnings.Count == 0 ? string.Empty : "\n" + string.Join("\n", warnings.Distinct(StringComparer.Ordinal));
        AutomaticBackupText = _lastBackupSummary + _lastBackupWarnings;
        ShowAutomaticBackupWarning = warnings.Count > 0;
    }
    private void MarkBackupUnknown(string reason)
    {
        AutomaticBackupText = string.IsNullOrEmpty(_lastBackupSummary) ? reason :
            _lastBackupSummary + _lastBackupWarnings + "\n以上为上次观察结果，当前状态未核实。\n" + reason;
        ShowAutomaticBackupWarning = true;
    }
    private static string Size(long? bytes) => bytes is null || bytes < 0 ? "未核实" :
        (bytes.Value / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB";
    private static string MaintenancePhase(BackupMaintenancePhase phase) => phase switch
    {
        BackupMaintenancePhase.Disabled => "未启用", BackupMaintenancePhase.Waiting => "等待下一周期",
        BackupMaintenancePhase.Running => "进行中", BackupMaintenancePhase.Completed => "完成",
        BackupMaintenancePhase.Deferred => "等待条件或重试", _ => "失败"
    };
}
