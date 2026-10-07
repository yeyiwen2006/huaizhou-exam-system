using System.Windows.Threading;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Reporting;
using Exam.Domain.Reporting;

namespace Exam.Admin.Reporting;

public sealed partial class ReportPageViewModel : BindableBase, IDisposable
{
    private readonly AdminApiClient _api;
    private readonly IFileDialogService _files;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource _context = new();
    private string _examId = string.Empty;
    private string _status = "请选择已结束的考试。";
    private string? _snapshot;
    private ReportStatus? _observedStatus;
    private bool _autoLoad = true;
    private bool _connected, _active, _busy, _polling, _disposed;
    private long _generation;
    private IReadOnlyList<ReportMissingParticipant> _missing = [];
    public ReportTableSessionViewModel Session { get; } = new();

    public ReportPageViewModel(AdminApiClient api, IFileDialogService files)
    {
        _api = api; _files = files;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_disposed && _connected && !_busy && !string.IsNullOrWhiteSpace(ExamId));
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !_disposed && _connected && !_busy && Session.HasTables && _snapshot is not null);
        CancelCommand = new RelayCommand(() => { _autoLoad = false; Reset("操作已取消，请手动刷新报表。"); }, () => !_disposed && _busy);
        _api.ConfigurationChanged += ConfigurationChanged;
        _timer.Tick += Tick;
    }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }
    public string ExamId { get => _examId; set { if (SetProperty(ref _examId, value)) { _autoLoad = true; Reset("考试已切换，请刷新报表。"); } } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IReadOnlyList<ReportMissingParticipant> MissingParticipants
    {
        get => _missing;
        private set { if (SetProperty(ref _missing, value)) OnPropertyChanged(nameof(HasMissingParticipants)); }
    }
    public bool HasMissingParticipants => _missing.Count > 0;
    public bool ConnectionReady { get => _connected; set { if (SetProperty(ref _connected, value)) { _autoLoad = true; Reset("连接状态已变化，请刷新报表。"); UpdateTimer(); } } }
    public bool IsPageActive { get => _active; set { if (SetProperty(ref _active, value)) { _autoLoad = true; if (!value) Reset("报表页已关闭，请刷新报表。"); UpdateTimer(); } } }
    private void UpdateTimer() { if (_connected && _active && !_disposed) _timer.Start(); else _timer.Stop(); }
    private void Commands() { RefreshCommand.RaiseCanExecuteChanged(); ExportCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); NotifyPresentation(); }
    private void ConfigurationChanged(object? sender, EventArgs e) => Reset("身份或服务器已变化，请重新加载报表。");
    private void Reset(string reason)
    {
        _generation++;
        var old = _context; _context = new(); old.Cancel(); old.Dispose();
        _snapshot = null; _observedStatus = null; Session.Invalidate(); MissingParticipants = []; _busy = false; Status = reason; Commands();
    }
    private void ApplyStatus(ReportStatus status)
    {
        MissingParticipants = status.MissingParticipants;
        NotifyPresentation();
        Status = $"{status.Reason} 实际参加考试 {status.ParticipantCount}，已回收 {status.CollectedCount}，已完成计分 {status.GradedCount}。";
    }
    public async Task RefreshAsync()
    {
        if (_busy || !_connected || string.IsNullOrWhiteSpace(ExamId) || _disposed) return;
        var generation = _generation; var exam = ExamId; var token = _context.Token;
        var ticket = Session.BeginLoad(exam);
        _autoLoad = true; _snapshot = null; _observedStatus = null; _busy = true; Commands();
        try
        {
            var status = await _api.GetReportStatusAsync(exam, token);
            if (generation != _generation) return;
            if (_observedStatus is { } observed && !SameStatus(observed, status))
            { Reset("加载期间报表状态已变化。"); _observedStatus = observed; ApplyStatus(observed); return; }
            _observedStatus = status;
            ApplyStatus(status);
            if (status.State != ReportState.Ready || status.SnapshotId is null)
            { Session.Invalidate(); return; }
            var published = await _api.GetReportAsync(exam, status.SnapshotId, token);
            if (generation != _generation) return;
            if (Session.TryAcceptPublished(ticket, ReportJson.Read(published))) _snapshot = status.SnapshotId;
            else Status = "报表结果未通过校验，请重新刷新。";
        }
        catch (OperationCanceledException) { if (generation == _generation) { Session.Invalidate(); Status = "报表加载已取消。"; } }
        catch (Exception) { if (generation == _generation) { Session.TryFail(ticket); Status = "报表加载失败，请检查连接并重新刷新。"; } }
        finally { if (generation == _generation) { _busy = false; Commands(); } }
    }
    public async Task ExportAsync()
    {
        if (_busy || _snapshot is null || !_connected || _disposed) return;
        var generation = _generation; var exam = ExamId; var snapshot = _snapshot; var token = _context.Token;
        _busy = true; Commands();
        try
        {
            var destination = _files.SaveExcelFile("考试报表.xlsx");
            if (generation != _generation || _snapshot != snapshot) return;
            if (destination is null) { Status = "已取消选择导出文件。"; return; }
            Status = "正在生成并下载完整 Excel…";
            await _api.ExportReportAsync(exam, snapshot, destination, token);
            if (generation == _generation) Status = "Excel 已完整保存。";
        }
        catch (OperationCanceledException) { if (generation == _generation) Status = "导出已取消，目标原文件已保留。"; }
        catch (Exception) { if (generation == _generation) { Session.Invalidate(); _snapshot = null; Status = "导出失败，目标原文件已保留；请刷新报表。"; } }
        finally { if (generation == _generation) { _busy = false; Commands(); } }
    }
    private async void Tick(object? sender, EventArgs e) => await PollAsync();
    public async Task PollAsync()
    {
        if (_polling || !_connected || !_active || string.IsNullOrWhiteSpace(ExamId) || _disposed) return;
        _polling = true;
        var generation = _generation;
        var refresh = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_context.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var status = await _api.GetReportStatusAsync(ExamId, deadline.Token).WaitAsync(deadline.Token);
            if (generation != _generation) return;
            if (_observedStatus is { } observed && !SameStatus(observed, status))
            { Reset("报表版本或回收状态已变化。"); _observedStatus = status; ApplyStatus(status); }
            else
            {
                _observedStatus = status;
                if (!_busy && _autoLoad) ApplyStatus(status);
                refresh = !_busy && _snapshot is null && _autoLoad && status.State == ReportState.Ready;
            }
        }
        catch (OperationCanceledException) { if (generation == _generation) Reset("状态查询已超时或取消，旧报表已隐藏。"); }
        catch (Exception) { if (generation == _generation) Reset("连接或授权失效，旧报表已隐藏。"); }
        finally { _polling = false; }
        if (refresh && generation == _generation) await RefreshAsync();
    }
    private static bool SameStatus(ReportStatus left, ReportStatus right) =>
        left.ExamId == right.ExamId && left.State == right.State && left.SnapshotId == right.SnapshotId &&
        left.RosterVersion == right.RosterVersion && left.PaperVersion == right.PaperVersion &&
        left.GradingVersion == right.GradingVersion && left.ParticipantCount == right.ParticipantCount &&
        left.CollectedCount == right.CollectedCount && left.GradedCount == right.GradedCount;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= Tick; _api.ConfigurationChanged -= ConfigurationChanged;
        _generation++; _context.Cancel(); _context.Dispose(); Session.Dispose(); Commands();
    }
}
