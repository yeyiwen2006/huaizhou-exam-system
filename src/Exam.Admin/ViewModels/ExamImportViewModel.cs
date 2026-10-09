using Exam.Infrastructure.Packaging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Exams;
using Exam.Contracts.Versioning;
using Exam.Desktop.Shared.Controls;

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
    private string _publishReason = string.Empty;
    private string _changedQuestionsText = string.Empty;
    private string _changedQuestionsHint = string.Empty;
    private readonly List<PreparedExamImport> _retired = [];
    private long? _paperVersion;
    private long _publicationRead;
    private bool _readingPublication;

    public ExamImportViewModel(AdminApiClient api, IExamImportSourceDialog? dialog = null,
        IExamImportService? service = null)
    {
        _api = api;
        _service = service ?? new ExamImportService(api);
        _dialog = dialog ?? new FileDialogService();
        _api.ConfigurationChanged += OnConfigurationChanged;
        SelectDirectoryCommand = new RelayCommand(() => Select(_dialog.OpenDirectory()), () => CanEditRevisionDetails);
        SelectZipCommand = new RelayCommand(() => Select(_dialog.OpenZip()), () => CanEditRevisionDetails);
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => CanEditRevisionDetails &&
            !string.IsNullOrWhiteSpace(ExamId) && !string.IsNullOrWhiteSpace(SourcePath), SetError);
        PublishCommand = new AsyncRelayCommand(PublishAsync, () => CanPublish, SetError);
        RetrieveMaterialsCommand = new AsyncRelayCommand(RetrieveMaterialsAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(ExamId), SetError);
        OpenQuestionCommand = new RelayCommand(OpenQuestion, () => !IsBusy && SelectedQuestion is not null && HasPreview);
        CancelCommand = new RelayCommand(() => _operationCancellation?.Cancel(), () => IsBusy);
        RefreshPublicationCommand = new AsyncRelayCommand(RefreshPublicationAsync,
            () => !_disposed && _api.IsConfigured && !string.IsNullOrEmpty(ExamId) && !IsBusy && !_readingPublication);
    }

    public Func<string, Task<bool>> ConfirmPublicationAsync { get; set; } = _ => Task.FromResult(false);
    public string ExamId => _examId;
    public long? PaperVersion => _paperVersion;
    public string PublicationText => _readingPublication ? "正在读取发布状态…" : _paperVersion switch
    {
        > 0 => "试卷已发布",
        0 => "试卷未发布",
        _ => "发布状态未确认",
    };
    public StatusLevel PublicationLevel => _paperVersion switch
    {
        > 0 => StatusLevel.Success,
        0 => StatusLevel.Neutral,
        _ => StatusLevel.Warning,
    };
    public event Action<string, long?>? PaperPublicationChanged;
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
    public bool CanEditRevisionDetails => !IsBusy && _preview?.PublicationAttempted != true;
    public bool CanPublish => !_disposed && HasPreview && !IsBusy && _preview!.Expected.ExamId == ExamId;

    /// <summary>本次修改原因：开考后修订必须填写，随发布一起记录。</summary>
    public string PublishReason
    {
        get => _publishReason;
        set
        {
            if (_preview?.PublicationAttempted == true) return;
            if (SetProperty(ref _publishReason, value)) ClearPreview("修改原因已改变，请重新检查后发布。");
        }
    }

    /// <summary>
    /// 变更题号（空格、逗号或顿号分隔）。留空＝按内容自动算出；开考后修订会以这里声明的为准，
    /// 服务器按内容核对，不一致就拒绝发布。
    /// </summary>
    public string ChangedQuestionsText
    {
        get => _changedQuestionsText;
        set
        {
            if (_preview?.PublicationAttempted == true) return;
            if (SetProperty(ref _changedQuestionsText, value)) ClearPreview("声明变更题号已改变，请重新检查后发布。");
        }
    }

    /// <summary>检查时按内容算出的差异提示（只读）：帮操作者确认，不代替服务器的核对。</summary>
    public string ChangedQuestionsHint
    {
        get => _changedQuestionsHint;
        private set => SetProperty(ref _changedQuestionsHint, value);
    }
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
    public AsyncRelayCommand RetrieveMaterialsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand RefreshPublicationCommand { get; }

    public async Task RefreshPublicationAsync()
    {
        if (_disposed || IsBusy || !_api.IsConfigured || string.IsNullOrEmpty(ExamId)) return;
        var read = ++_publicationRead;
        var examId = ExamId;
        _readingPublication = true;
        NotifyPublication();
        try
        {
            var detail = await _api.GetExamDetailAsync(examId);
            if (_disposed || read != _publicationRead || examId != ExamId) return;
            ApplyPaperVersion(detail.Summary.PaperVersion);
        }
        catch
        {
            if (!_disposed && read == _publicationRead && examId == ExamId) ApplyPaperVersion(null);
        }
        finally
        {
            if (read == _publicationRead) { _readingPublication = false; NotifyPublication(); }
        }
    }

    private void ApplyPaperVersion(long? version, bool notify = true)
    {
        ++_publicationRead;
        _readingPublication = false;
        _paperVersion = version is >= 0 ? version : null;
        NotifyPublication();
        if (notify && !string.IsNullOrEmpty(ExamId)) PaperPublicationChanged?.Invoke(ExamId, _paperVersion);
    }

    private void NotifyPublication()
    {
        OnPropertyChanged(nameof(PaperVersion));
        OnPropertyChanged(nameof(PublicationText));
        OnPropertyChanged(nameof(PublicationLevel));
        RefreshPublicationCommand.RaiseCanExecuteChanged();
    }

    public void SetCurrentExam(ExamSummary? exam)
    {
        ApplyPaperVersion(exam?.PaperVersion, notify: false);
        if (exam is not null && StringComparer.Ordinal.Equals(_examId, exam.ExamId))
        {
            var settingsChanged = _settingsVersion != exam.SettingsVersion;
            var nameChanged = !StringComparer.Ordinal.Equals(_examName, exam.Name);
            _settingsVersion = exam.SettingsVersion;
            ExamName = exam.Name;
            // Saving this exam is not a context switch. Compilation finishes against the
            // latest settings; an in-flight publication retains its original request.
            if ((settingsChanged || nameChanged) && !IsBusy && _preview?.PublicationAttempted != true)
                ClearPreview("考试设置已更新，请重新检查材料；已保留导入源。");
            return;
        }
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
        if (_preview?.PublicationAttempted == true)
        { Status = "此前发布结果尚待核对，请保留同一预览重试，不能重新检查生成另一请求。"; return; }
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
            prepared = await _service.PrepareAsync(source, examId, _operationCancellation.Token,
                string.IsNullOrWhiteSpace(PublishReason) ? null : PublishReason, ParseChangedQuestions());
            if (context != _context || examId != ExamId || source != SourcePath) return;
            _preview = prepared;
            prepared = null;
            foreach (var question in _preview.Questions) Questions.Add(question);
            SelectedQuestion = Questions.FirstOrDefault();
            ChangedQuestionsHint = _preview.IsRevision
                ? (_preview.ChangedQuestionsHint.Count == 0
                    ? "内容没有变化"
                    : "按内容算出有变化的是：" + string.Join("、", _preview.ChangedQuestionsHint) +
                      "；将声明：" + string.Join("、", _preview.DeclaredChangedQuestions))
                : string.Empty;
            Status = _preview.IsRevision
                ? $"检查通过：{ExamName}，{Questions.Count} 道题；这是开考后修订（试卷版本 {_preview.Expected.Versions.PaperVersion} → {_preview.TargetPaperVersion}）。请逐题打开 PDF 确认版式与答案，再确认发布。"
                : $"检查通过：{ExamName}，{Questions.Count} 道题；已核对答案、编译和选项定位。请逐题打开 PDF 查看版式，确认后才能发布。";
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

    /// <summary>把界面里填的变更题号切成题号列表；空表示按内容自动算出。</summary>
    internal IReadOnlyList<string> ParseChangedQuestions() =>
        _changedQuestionsText
            .Split(ChangedQuestionSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>变更题号的分隔符：空格、逗号、顿号与分号的中英文写法都接受。</summary>
    private static readonly char[] ChangedQuestionSeparators =
        [' ', '\t', '\r', '\n', ',', '，', '、', ';', '；'];


    /// <summary>
    /// 取回当前已发布版本的源材料：开考后修订要从那一版材料出发。
    /// 取回目录在所选父目录下新建（含题目源文件与独立答案），已存在就报错不覆盖。
    /// </summary>
    public async Task RetrieveMaterialsAsync()
    {
        if (IsBusy || _disposed) return;
        if (string.IsNullOrWhiteSpace(ExamId)) { Status = "请先选择考试。"; return; }
        var parent = _dialog.OpenNewMaterialDirectory();
        if (string.IsNullOrWhiteSpace(parent)) return;
        var context = _context;
        var examId = ExamId;
        var examName = ExamName;
        _operationCancellation = new CancellationTokenSource();
        IsChecking = true;
        Status = "正在取回当前已发布版本的源材料…";
        try
        {
            var snapshot = await _api.GetExamAsync(examId, _operationCancellation.Token);
            if (context != _context) return;
            if (snapshot.Versions.PaperVersion < 1)
            {
                Status = "这场考试还没有已发布的试卷版本，没有可取回的源材料。";
                return;
            }

            var archive = await _api.DownloadPaperMaterialsAsync(examId, snapshot.Versions.PaperVersion,
                _operationCancellation.Token);
            if (snapshot.Versions.GradingVersion > snapshot.Versions.PaperVersion)
            {
                var currentRules = await _api.GetGradingMaterialsAsync(examId, _operationCancellation.Token);
                if (context != _context) return;
                archive = PaperRevisionMaterials.WithCurrentRules(archive, currentRules, examId,
                    snapshot.Versions.PaperVersion, snapshot.Versions.GradingVersion);
            }
            var read = Exam.Infrastructure.Packaging.PaperMaterialsArchive.Read(archive);
            if (!read.Succeeded)
                throw new InvalidDataException(read.Failure?.Value ?? "服务器返回的归档不可用。");
            if (context != _context) return;
            var target = Path.Combine(parent, $"{examName}-源材料-v{snapshot.Versions.PaperVersion}");
            await Exam.Infrastructure.Packaging.PaperMaterialsArchive.ExtractAsync(read, target,
                _operationCancellation.Token);
            if (context != _context) return;
            Status = $"已取回试卷版本 {snapshot.Versions.PaperVersion} 的源材料（{read.Manifest!.Files.Count} 个文件）到 {target}；" +
                "该目录含独立答案，请只在本机保存；改完后用它重新检查。";
        }
        catch (OperationCanceledException) { if (context == _context) Status = "取回已取消。"; }
        catch (Exception error)
        {
            if (context == _context) Status = "取回源材料失败：" + FriendlyErrors.Describe(error);
        }
        finally
        {
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
            var revisionDetail = prepared.IsRevision
                ? $"\n本次为开考后修订：试卷版本 {prepared.Expected.Versions.PaperVersion} → {prepared.TargetPaperVersion}" +
                  $"\n声明变更题号：{string.Join("、", prepared.DeclaredChangedQuestions)}" +
                  $"\n修改原因：{prepared.Reason}"
                : string.Empty;
            var confirmed = await ConfirmPublicationAsync($"考试：{ExamName}\n来源：{prepared.Source}\n题目：{prepared.Questions.Count} 道{revisionDetail}\n确认已逐题查看版式、答案及选项绑定并正式发布？");
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
            {
                ApplyPaperVersion(null);
                Status = "发布结果尚不能确认；没有显示成功。请保留此预览，连接原服务器后用同一预览重试。";
            }
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
        { ApplyPaperVersion(null); Status = "服务器返回的试卷或规则版本与本次导入不符；请人工核对，不能认定发布成功。"; return; }
        ClearPreview("已发布");
        ApplyPaperVersion(actual.Versions.PaperVersion);
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
        _operationCancellation?.Cancel();
        ChangedQuestionsHint = string.Empty;
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
        RetrieveMaterialsCommand.RaiseCanExecuteChanged();
        RefreshPublicationCommand.RaiseCanExecuteChanged();
        SelectDirectoryCommand.RaiseCanExecuteChanged();
        SelectZipCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEditRevisionDetails));
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
