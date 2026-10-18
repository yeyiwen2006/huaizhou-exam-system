using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Grading;
using Exam.Infrastructure.Packaging;

namespace Exam.Admin.ViewModels;

/// <summary>Check an immutable rule snapshot, confirm it, and keep its request after an uncertain response.</summary>
public sealed class GradingRevisionViewModel : BindableBase, IDisposable
{
    private const string NoExamStatus = "请先在「全部考试」中打开一场考试，再改答案与计分规则。";
    private readonly AdminApiClient _api;
    private readonly IExamImportSourceDialog _dialog;
    private string _examId = string.Empty, _packagePath = string.Empty, _reason = string.Empty;
    private string _status = NoExamStatus, _publishedText = "尚未发布规则修订。", _recalculationText = "尚未核对重算状态。";
    private bool _busy, _completed, _disposed;
    private long _context, _publishedVersion;
    private long _historyRead;
    private string _historyStatus = "请选择考试以查看版本记录。";
    private CancellationTokenSource? _cancellation;
    private PreparedGradingRevision? _pending;

    public GradingRevisionViewModel(AdminApiClient api, IExamImportSourceDialog? dialog = null)
    {
        _api = api; _dialog = dialog ?? new FileDialogService();
        SelectPackageCommand = new AsyncRelayCommand(SelectPackageAsync, () => CanSelectPackage, SetError);
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => CanCheck, SetError);
        PublishCommand = new AsyncRelayCommand(PublishAsync, () => CanPublish, SetError);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync, () => CanRefreshStatus, SetError);
        _api.ConfigurationChanged += ConnectionChanged;
    }

    public Func<string, Task<bool>> ConfirmPublicationAsync { get; set; } = message => Task.FromResult(
        MessageBox.Show(message, "确认答案与计分规则修订", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes);
    public ObservableCollection<GradingRevisionQuestion> Questions { get; } = [];
    public ObservableCollection<GradingVersionHistoryRow> History { get; } = [];
    public string HistoryStatus { get => _historyStatus; private set => SetProperty(ref _historyStatus, value); }
    public string ExamId
    {
        get => _examId;
        set { if (SetProperty(ref _examId, value)) ResetContext(); }
    }
    public string PackagePath { get => _packagePath; private set => SetProperty(ref _packagePath, value); }
    public string Reason
    {
        get => _reason;
        set
        {
            if (!CanEditRevision) return;
            if (SetProperty(ref _reason, value)) InvalidatePreview();
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string PublishedText { get => _publishedText; private set => SetProperty(ref _publishedText, value); }
    public string RecalculationText { get => _recalculationText; private set => SetProperty(ref _recalculationText, value); }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) RaiseCommandStates(); } }
    public bool CanEditRevision => !IsBusy && !_disposed && _pending?.PublicationAttempted != true;
    public bool CanSelectPackage => CanEditRevision && !string.IsNullOrWhiteSpace(ExamId);
    public bool CanCheck => CanSelectPackage && !string.IsNullOrWhiteSpace(PackagePath);
    public bool CanPublish => !IsBusy && !_disposed && !_completed && _pending?.Request.ExamId == ExamId;
    public bool CanRefreshStatus => !IsBusy && !_disposed && !string.IsNullOrWhiteSpace(ExamId);
    public AsyncRelayCommand SelectPackageCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public AsyncRelayCommand RefreshStatusCommand { get; }

    public Task SelectPackageAsync()
    {
        if (!CanSelectPackage) { if (string.IsNullOrWhiteSpace(ExamId)) Status = NoExamStatus; return Task.CompletedTask; }
        var path = _dialog.OpenDirectory();
        if (!string.IsNullOrWhiteSpace(path))
        {
            InvalidatePreview(); PackagePath = Path.GetFullPath(path);
            Status = "已选择材料包目录；请检查并核对逐题答案、满分后确认发布。";
        }
        return Task.CompletedTask;
    }

    private bool CheckInputs()
    {
        if (string.IsNullOrWhiteSpace(ExamId)) { Status = NoExamStatus; return false; }
        if (string.IsNullOrWhiteSpace(PackagePath)) { Status = "请先选择材料包目录（含 manifest.json 与 server-only/answer-key.json）。"; return false; }
        if (Reason.Length > 1000) { Status = "本次修改备注不能超过 1000 字。"; return false; }
        if (!File.Exists(Path.Combine(PackagePath, "manifest.json")) || !File.Exists(Path.Combine(PackagePath, "server-only", "answer-key.json")))
        { Status = "材料包里找不到 manifest.json 或 server-only/answer-key.json；换规则必须带完整参考答案。"; return false; }
        return true;
    }

    public async Task CheckAsync()
    {
        if (!CanEditRevision || !CheckInputs()) return;
        InvalidatePreview();
        var context = _context; var exam = ExamId; var path = PackagePath; var reason = Reason;
        using var cancellation = StartOperation();
        IsBusy = true; Status = "正在核对本场试卷结构、完整答案与全部计分分支…";
        try
        {
            var prepared = await PreparedGradingRevision.PrepareAsync(_api, exam, path, reason, cancellation.Token);
            if (!Current(context)) { prepared.Dispose(); return; }
            _pending = prepared;
            foreach (var question in prepared.Preview) Questions.Add(question);
            Status = $"检查通过：{prepared.Preview.Count} 题，规则版本 {prepared.Request.ExpectedVersion} → {prepared.Request.ExpectedVersion + 1}。尚未发布；请核对下方答案与满分。";
        }
        catch (OperationCanceledException) { if (Current(context)) Status = "核对已取消，未发布。"; }
        catch (Exception error) { if (Current(context)) SetError(error); }
        finally { EndOperation(cancellation); IsBusy = false; }
    }

    public async Task PublishAsync()
    {
        if (IsBusy || _disposed || _completed) return;
        if (_pending is null) { if (CheckInputs()) Status = "请先检查并核对规则预览，再确认发布。"; return; }
        if (_pending.Request.ExamId != ExamId)
        { Status = "另一场考试的发布结果尚未确认，请返回原考试重试同一份规则预览。"; return; }
        var prepared = _pending; var context = _context; var exam = ExamId;
        using var cancellation = StartOperation();
        IsBusy = true;
        try
        {
            if (!prepared.PublicationAttempted && !await ConfirmPublicationAsync(
                    $"已核对 {prepared.Preview.Count} 道题的答案与满分？\n本次只发布规则版本 {prepared.Request.ExpectedVersion + 1}，不更换试卷。\n修改原因：{prepared.Request.Reason}\n发布后旧报表失效，全部已收答卷按新规则重算。")) return;
            if (!Current(context)) return;
            var published = await prepared.PublishAsync(_api, cancellation.Token);
            if (ReferenceEquals(_pending, prepared)) { _pending = null; prepared.Dispose(); }
            if (!Current(context)) return;
            _completed = true; _publishedVersion = published.Versions.GradingVersion;
            PublishedText = $"规则发布成功：规则版本 {published.Versions.GradingVersion}，试卷版本 {published.Versions.PaperVersion}。" +
                "发布成功只说明服务器已切换规则版本，成绩是否重算完见下一行。";
            Status = "规则修订已提交；重算期间旧报表不可查询或导出。";
            await RefreshHistoryAsync();
            try { await ReadStatusAsync(exam, context, cancellation.Token); }
            catch (Exception) { if (Current(context)) RecalculationText = "重算状态核对失败（发布结果不受影响），请刷新重算状态。"; }
        }
        catch (OperationCanceledException) { if (Current(context)) Status = "操作已中止，发布结果尚未确认；请重试同一份规则预览。"; }
        catch (Exception error)
        {
            if (Current(context))
            {
                if (error is AdminApiException rejection && rejection.Code is "VERSION_CONFLICT" or "INVALID_INPUT" or "PUBLICATION_NOT_READY")
                {
                    if (ReferenceEquals(_pending, prepared)) { _pending = null; prepared.Dispose(); }
                    Questions.Clear(); _completed = false;
                    Status = rejection.Code == "VERSION_CONFLICT"
                        ? "考试或规则已被更新，服务器拒绝了本次修订；请重新取回材料并检查，不能覆盖其他修改。"
                        : "服务器未接受本次修订；请核对完整答案、计分规则和已发布试卷后重新检查。";
                }
                else
                {
                    SetError(error);
                    if (prepared.PublicationAttempted) Status += " 发布结果尚未确认，保留同一预览重试，不重新读取版本。";
                }
            }
        }
        finally { EndOperation(cancellation); IsBusy = false; }
    }

    public async Task RefreshStatusAsync()
    {
        if (!CanRefreshStatus) return;
        var context = _context; var exam = ExamId;
        using var cancellation = StartOperation(); IsBusy = true;
        try { await ReadStatusAsync(exam, context, cancellation.Token); await RefreshHistoryAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (Current(context)) RecalculationText = "重算状态核对失败（发布结果不受影响），请稍后刷新。"; }
        finally { EndOperation(cancellation); IsBusy = false; }
    }
    private async Task ReadStatusAsync(string exam, long context, CancellationToken ct)
    {
        var report = await _api.GetGradingStatusAsync(exam, ct);
        if (!Current(context)) return;
        if (report.ExamId != exam || report.Readiness is { } readiness && readiness.TargetVersion != report.CurrentGradingVersion ||
            _publishedVersion > 0 && (report.CurrentGradingVersion ?? 0) < _publishedVersion)
            throw new InvalidDataException("重算状态不属于本次考试或版本。");
        RecalculationText = Describe(report);
    }

    internal static string Describe(GradingStatusReport report)
    {
        if (report.CurrentGradingVersion is not { } version) return "尚未发布任何计分规则：没有可核对的结果版本。";
        if (report.Readiness is not { } readiness) return $"已发布规则版本 {version}，但本次读取没有就绪结论。";
        var scope = $"范围 {report.FinalSubmissionCount} 份最终答卷，已按版本 {version} 计分 {report.GradedAtCurrentVersion} 份。";
        if (readiness.RecalculationReady) return $"结果就绪：规则版本 {version}，{scope}";
        var blockers = readiness.Blockers.Count == 0 ? string.Empty : " 阻断：" + string.Join("；", readiness.Blockers);
        return $"重算中：规则版本 {version}，{scope} 未完成 {readiness.Outstanding.Count} 份。{blockers}".TrimEnd();
    }
    private void InvalidatePreview()
    {
        if (_pending?.PublicationAttempted == true) return;
        _pending?.Dispose(); _pending = null; _completed = false; Questions.Clear(); RaiseCommandStates();
    }
    private void ResetContext()
    {
        ++_context; _cancellation?.Cancel();
        InvalidatePreview(); Questions.Clear(); PackagePath = string.Empty; _publishedVersion = 0; _completed = false;
        if (_pending?.Request.ExamId == ExamId) foreach (var q in _pending.Preview) Questions.Add(q);
        PublishedText = "尚未发布规则修订。"; RecalculationText = "尚未核对重算状态。";
        ++_historyRead; History.Clear();
        HistoryStatus = string.IsNullOrWhiteSpace(ExamId) ? "请选择考试以查看版本记录。" : "正在读取版本记录…";
        if (!string.IsNullOrWhiteSpace(ExamId) && _api.IsConfigured) _ = RefreshHistoryAsync();
        Status = _pending?.PublicationAttempted == true ? "保留了尚未确认的原请求；回到原考试后可重试，不能重新检查生成另一请求。" :
            string.IsNullOrWhiteSpace(ExamId) ? NoExamStatus : "已选择考试；请选择材料包目录并检查。";
        RaiseCommandStates();
    }
    public async Task RefreshHistoryAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(ExamId)) return;
        var exam = ExamId; var context = _context; var read = ++_historyRead;
        try
        {
            var history = await _api.GetGradingHistoryAsync(exam);
            if (!Current(context) || read != _historyRead) return;
            History.Clear();
            foreach (var row in history.OrderByDescending(row => row.GradingVersion))
                History.Add(new(row.GradingVersion, row.PaperVersion,
                    row.ChangedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), row.Reason));
            HistoryStatus = History.Count == 0 ? "尚未发布答案与计分规则。" : $"共 {History.Count} 个规则版本。";
        }
        catch (Exception)
        { if (Current(context) && read == _historyRead) HistoryStatus = "版本记录读取失败，请稍后刷新。"; }
    }
    private void ConnectionChanged(object? sender, EventArgs e) => ResetContext();
    internal void InvalidateForArchiveReplacement(string examId)
    {
        if (_pending?.Request.ExamId == examId) { _pending.Dispose(); _pending = null; }
        if (ExamId != examId) return;
        ResetContext();
        Status = "本场考试已被归档覆盖，请重新读取答案和计分规则。";
    }
    private bool Current(long context) => !_disposed && context == _context;
    private CancellationTokenSource StartOperation() { var c = new CancellationTokenSource(); _cancellation = c; return c; }
    private void EndOperation(CancellationTokenSource c) { if (ReferenceEquals(_cancellation, c)) _cancellation = null; }
    private void RaiseCommandStates()
    {
        foreach (var property in new[] { nameof(CanEditRevision), nameof(CanSelectPackage), nameof(CanCheck), nameof(CanPublish), nameof(CanRefreshStatus) }) OnPropertyChanged(property);
        SelectPackageCommand.RaiseCanExecuteChanged(); CheckCommand.RaiseCanExecuteChanged();
        PublishCommand.RaiseCanExecuteChanged(); RefreshStatusCommand.RaiseCanExecuteChanged();
    }
    private void SetError(Exception error) => Status = error is AdminApiException api ? api.Code switch
    {
        "REPLICA_UNAVAILABLE" or "PERSISTENCE_FAILED" => "服务器尚未确认保存结果，请保留原预览后重试。",
        _ => FriendlyErrors.Describe(api.Code, null)
    } :
        error is InvalidDataException or ArgumentException ? "规则材料或版本核对未通过，请检查清单、答案与本场试卷结构。" : "操作未完成，请核对连接与本机材料后重试。";
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; ++_context; _cancellation?.Cancel();
        _api.ConfigurationChanged -= ConnectionChanged; _pending?.Dispose();
    }
}

public sealed record GradingVersionHistoryRow(long GradingVersion, long PaperVersion, string ChangedAt, string Reason);
