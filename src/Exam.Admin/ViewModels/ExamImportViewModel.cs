using Exam.Infrastructure.Packaging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Exams;
using Exam.Contracts.Versioning;

namespace Exam.Admin.ViewModels;

/// <summary>One examination, one immutable checked local snapshot, one explicit publication confirmation.</summary>
public sealed class ExamImportViewModel : BindableBase, IDisposable
{
    private readonly AdminApiClient _api;
    private readonly IExamImportService _service;
    private readonly IExamImportSourceDialog _dialog;
    private PreparedExamImport? _preview;
    private string _examId = string.Empty;
    private string _examName = string.Empty;
    private string _sourcePath = string.Empty;
    private string _status = "请先选择考试，再选择本地试题目录或 ZIP。";
    private bool _isChecking;
    private bool _isPublishing;
    private long _context;
    private long _settingsVersion = -1;
    private bool _disposed;
    private CancellationTokenSource? _operationCancellation;
    private ImportPreviewQuestion? _selectedQuestion;
    private readonly List<PreparedExamImport> _retired = [];

    public ExamImportViewModel(AdminApiClient api, IExamImportSourceDialog? dialog = null,
        IExamImportService? service = null)
    {
        _api = api;
        _service = service ?? new ExamImportService(api);
        _dialog = dialog ?? new FileDialogService();
        _api.ConfigurationChanged += OnConfigurationChanged;
        SelectDirectoryCommand = new RelayCommand(() => Select(_dialog.OpenDirectory()), () => !IsBusy);
        SelectZipCommand = new RelayCommand(() => Select(_dialog.OpenZip()), () => !IsBusy);
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy &&
            !string.IsNullOrWhiteSpace(ExamId) && !string.IsNullOrWhiteSpace(SourcePath), SetError);
        PublishCommand = new AsyncRelayCommand(PublishAsync, () => CanPublish, SetError);
        OpenQuestionCommand = new RelayCommand(OpenQuestion, () => !IsBusy && SelectedQuestion is not null && HasPreview);
        CancelCommand = new RelayCommand(() => _operationCancellation?.Cancel(), () => IsBusy);
    }

    public Func<string, Task<bool>> ConfirmPublicationAsync { get; set; } = _ => Task.FromResult(false);
    public string ExamId => _examId;
    public string ExamName { get => _examName; private set => SetProperty(ref _examName, value); }
    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetProperty(ref _sourcePath, value)) ClearPreview("导入源已改变；旧检查结果已清除，请重新检查。");
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsChecking { get => _isChecking; private set { if (SetProperty(ref _isChecking, value)) UpdateCommands(); } }
    public bool IsPublishing { get => _isPublishing; private set { if (SetProperty(ref _isPublishing, value)) UpdateCommands(); } }
    public bool IsBusy => IsChecking || IsPublishing;
    public bool HasPreview => _preview is not null;
    public bool CanPublish => !_disposed && HasPreview && !IsBusy && _preview!.Expected.ExamId == ExamId;
    public ObservableCollection<ImportPreviewQuestion> Questions { get; } = [];
    public ImportPreviewQuestion? SelectedQuestion
    {
        get => _selectedQuestion;
        set { if (SetProperty(ref _selectedQuestion, value)) OpenQuestionCommand.RaiseCanExecuteChanged(); }
    }
    public RelayCommand SelectDirectoryCommand { get; }
    public RelayCommand SelectZipCommand { get; }
    public RelayCommand OpenQuestionCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public RelayCommand CancelCommand { get; }

    public void SetCurrentExam(ExamSummary? exam)
    {
        if (StringComparer.Ordinal.Equals(_examId, exam?.ExamId) && StringComparer.Ordinal.Equals(_examName, exam?.Name) &&
            _settingsVersion == (exam?.SettingsVersion ?? -1)) return;
        ClearPreview("考试已切换；旧检查结果已清除。");
        _settingsVersion = exam?.SettingsVersion ?? -1;
        _examId = exam?.ExamId ?? string.Empty;
        ExamName = exam?.Name ?? string.Empty;
        SourcePath = string.Empty;
        OnPropertyChanged(nameof(ExamId));
        UpdateCommands();
    }

    private void Select(string? path)
    {
        if (path is not null) SourcePath = path;
    }

    public async Task CheckAsync()
    {
        if (IsBusy || _disposed) return;
        ClearPreview("正在检查试题包…");
        var context = _context;
        var examId = ExamId;
        var source = SourcePath;
        if (string.IsNullOrWhiteSpace(examId) || string.IsNullOrWhiteSpace(source))
        { Status = "请先选择考试及试题目录或 ZIP。"; return; }
        _operationCancellation = new CancellationTokenSource();
        IsChecking = true;
        PreparedExamImport? prepared = null;
        try
        {
            prepared = await _service.PrepareAsync(source, examId, _operationCancellation.Token);
            if (context != _context || examId != ExamId || source != SourcePath) return;
            _preview = prepared;
            prepared = null;
            foreach (var question in _preview.Questions) Questions.Add(question);
            SelectedQuestion = Questions.FirstOrDefault();
            Status = $"检查通过：{ExamName}，{Questions.Count} 道题；已核对答案、编译和选项定位。请逐题打开 PDF 查看版式，确认后才能发布。";
            OnPropertyChanged(nameof(HasPreview));
            UpdateCommands();
        }
        catch (OperationCanceledException)
        { if (context == _context) Status = "检查已取消，未发布。"; }
        catch (Exception error)
        {
            if (context == _context) Status = "检查失败，未发布：" +
                (error is InvalidDataException or ArgumentException or InvalidOperationException
                    ? error.Message : FriendlyErrors.Describe(error));
        }
        finally
        {
            prepared?.Dispose();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            IsChecking = false;
        }
    }

    public async Task PublishAsync()
    {
        var prepared = _preview;
        if (_disposed || prepared is null || IsBusy || prepared.Expected.ExamId != ExamId) return;
        var context = _context;
        _operationCancellation = new CancellationTokenSource();
        // Confirmation is part of the critical section: a second click must not create another dialog/request.
        IsPublishing = true;
        try
        {
            var confirmed = await ConfirmPublicationAsync($"考试：{ExamName}\n来源：{prepared.Source}\n题目：{prepared.Questions.Count} 道\n确认已逐题查看版式、答案及选项绑定并正式发布？");
            if (context != _context || !ReferenceEquals(prepared, _preview)) return;
            if (!confirmed)
            {
                Status = prepared.PublicationAttempted ? "已取消本次重试；此前的发布结果仍须在原服务器核对。" : "已取消发布；服务器未收到发布请求。";
                return;
            }
            _operationCancellation.Token.ThrowIfCancellationRequested();
            Status = "正在加密并等待服务器确认整套发布结果，请勿重复操作。";
            var published = await _service.PublishAsync(prepared, _operationCancellation.Token);
            if (context == _context) CompleteIfMatched(prepared, published);
        }
        catch (Exception error)
        {
            if (context != _context) return;
            // Reading a locally committed version cannot prove the primary/standby confirmation completed.
            // Only the original upload's official response (including an idempotent replay) is success.
            if (error is HttpRequestException or OperationCanceledException ||
                error is AdminApiException apiError && ((int)apiError.StatusCode >= 500 ||
                    apiError.Code is "EMPTY_RESPONSE" or "INVALID_RESPONSE"))
                Status = "发布结果尚不能确认；没有显示成功。请保留此预览，连接原服务器后用同一预览重试。";
            else Status = "发布未确认生效：" + (error is InvalidDataException or ArgumentException or InvalidOperationException
                ? error.Message : FriendlyErrors.Describe(error));
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            IsPublishing = false;
            foreach (var retired in _retired) retired.Dispose();
            _retired.Clear();
        }
    }

    private void CompleteIfMatched(PreparedExamImport prepared, ExamVersionSnapshot actual)
    {
        if (!prepared.MatchesPublication(actual))
        { Status = "服务器返回的试卷或规则版本与本次导入不符；请人工核对，不能认定发布成功。"; return; }
        ClearPreview("已发布");
        Status = $"正式发布成功：考试 {ExamName}，试卷版本 {actual.Versions.PaperVersion}，规则版本 {actual.Versions.GradingVersion}。学生端更新状态需另行核对。";
    }

    private void OpenQuestion()
    {
        if (SelectedQuestion is null || !HasPreview) return;
        try { Process.Start(new ProcessStartInfo(SelectedQuestion.PdfPath) { UseShellExecute = true }); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Status = "无法打开题目 PDF，请检查本机默认 PDF 阅读器：" + FriendlyErrors.Describe(error); }
    }

    private void ClearPreview(string status)
    {
        ++_context;
        if (_preview is not null)
        {
            if (IsPublishing) _retired.Add(_preview);
            else _preview.Dispose();
        }
        _preview = null;
        Questions.Clear();
        SelectedQuestion = null;
        Status = status;
        OnPropertyChanged(nameof(HasPreview));
        UpdateCommands();
    }

    private void SetError(Exception error) => Status = "操作失败：" + FriendlyErrors.Describe(error);
    private void UpdateCommands()
    {
        CancelCommand.RaiseCanExecuteChanged();
        OpenQuestionCommand.RaiseCanExecuteChanged();
        CheckCommand.RaiseCanExecuteChanged();
        PublishCommand.RaiseCanExecuteChanged();
        SelectDirectoryCommand.RaiseCanExecuteChanged();
        SelectZipCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(IsBusy));
    }
    private void OnConfigurationChanged(object? sender, EventArgs e) => SetCurrentExam(null);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _api.ConfigurationChanged -= OnConfigurationChanged;
        ++_context;
        _operationCancellation?.Cancel();
        if (_preview is not null)
        {
            if (IsPublishing) _retired.Add(_preview);
            else _preview.Dispose();
            _preview = null;
        }
        if (!IsPublishing)
        {
            foreach (var retired in _retired) retired.Dispose();
            _retired.Clear();
        }
    }
}
