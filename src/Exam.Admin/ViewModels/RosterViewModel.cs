using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Administration;

namespace Exam.Admin.ViewModels;

public sealed class RosterViewModel : BindableBase, IDisposable
{
    private readonly RosterWorkbookService _workbooks;
    private readonly IFileDialogService _dialogs;
    private readonly AdminApiClient? _api;
    private readonly Func<string, bool> _confirmDelete;
    private string _name = string.Empty, _classText = string.Empty, _admissionNo = string.Empty;
    private string _status = "请选择一场考试，读取服务器名册。";
    private string _examId = string.Empty;
    private long _context;
    private bool _busy, _disposed;
    private CandidateRosterSnapshot? _snapshot;
    private RosterPreview? _pendingPreview;
    private RosterEntryDraft? _selectedEntry;

    // Without an API the component can inspect an Excel file, but never claims that a roster was saved.
    public RosterViewModel(RosterWorkbookService workbooks, IFileDialogService dialogs,
        AdminApiClient? api = null, Func<string, bool>? confirmDelete = null)
    {
        _workbooks = workbooks;
        _dialogs = dialogs;
        _api = api;
        _confirmDelete = confirmDelete ?? (message => MessageBox.Show(message, "删除考生",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        AddManualCommand = new AsyncRelayCommand(AddManualAsync, () => CanEdit);
        UpdateSelectedCommand = new AsyncRelayCommand(UpdateSelectedAsync, () => CanEdit && SelectedEntry is not null);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => CanEdit && SelectedEntry is not null);
        RefreshCommand = new AsyncRelayCommand(() => OpenAsync(_examId),
            () => !_busy && _api?.IsConfigured == true && !string.IsNullOrEmpty(_examId));
        CreateTemplateCommand = new RelayCommand(CreateTemplate);
        ImportPreviewCommand = new RelayCommand(ImportPreview, () => !_busy && (_api is null || CanEdit));
        ConfirmImportCommand = new AsyncRelayCommand(ConfirmImportAsync,
            () => CanEdit && _pendingPreview is { HasErrors: false, NewCount: > 0 });
        Entries.CollectionChanged += (_, _) => InvalidatePreview();
        if (_api is not null) _api.ConfigurationChanged += OnConnectionChanged;
    }

    public ObservableCollection<RosterEntryDraft> Entries { get; } = [];
    public ObservableCollection<RosterPreviewRow> PreviewRows { get; } = [];
    public string ExamId => _examId;
    public long? LoadedVersion => _snapshot?.Version;
    public bool CanEdit => !_disposed && !_busy && _api?.IsConfigured == true &&
        _snapshot is not null && StringComparer.Ordinal.Equals(_snapshot.ExamId, _examId);
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string ClassText { get => _classText; set => SetProperty(ref _classText, value); }
    public string AdmissionNo { get => _admissionNo; set => SetProperty(ref _admissionNo, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public RosterEntryDraft? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value)) return;
            if (value is not null) { Name = value.Name; ClassText = value.ClassText; AdmissionNo = value.AdmissionNo; }
            NotifyCommands();
        }
    }
    public AsyncRelayCommand AddManualCommand { get; }
    public AsyncRelayCommand UpdateSelectedCommand { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CreateTemplateCommand { get; }
    public RelayCommand ImportPreviewCommand { get; }
    public AsyncRelayCommand ConfirmImportCommand { get; }

    public void Reset(string examId = "")
    {
        _context++;
        _examId = examId;
        _snapshot = null;
        _busy = false;
        Entries.Clear();
        SelectedEntry = null;
        Name = ClassText = AdmissionNo = string.Empty;
        InvalidatePreview();
        Status = string.IsNullOrEmpty(examId) ? "请选择一场考试，读取服务器名册。" : "正在读取本场考试的服务器名册…";
        OnPropertyChanged(nameof(ExamId));
        NotifyCommands();
    }

    public async Task OpenAsync(string examId)
    {
        Reset(examId);
        if (_api?.IsConfigured != true || string.IsNullOrEmpty(examId) || _disposed) return;
        var context = _context;
        _busy = true;
        NotifyCommands();
        try
        {
            var snapshot = await _api.GetRosterAsync(examId);
            if (context != _context) return;
            Apply(snapshot);
            Status = $"已读取服务器名册，共 {Entries.Count} 人。";
        }
        catch when (context != _context) { }
        catch (Exception error) { Status = $"名册读取失败：{FriendlyErrors.Describe(error)} 请重新读取，当前不能写入。"; }
        finally { if (context == _context) { _busy = false; NotifyCommands(); } }
    }

    public async Task AddManualAsync()
    {
        if (!CanEdit) return;
        var row = InputRow();
        if (row is null) return;
        await ImportAsync([row]);
    }

    public async Task ConfirmImportAsync()
    {
        if (!CanEdit || _pendingPreview is not { HasErrors: false, NewCount: > 0 } preview) return;
        await ImportAsync(preview.Rows.Select(x => new RosterImportRow(x.RowNumber, x.Name, x.ClassText, x.AdmissionNo)).ToArray());
    }

    private async Task ImportAsync(IReadOnlyList<RosterImportRow> rows)
    {
        var context = _context;
        var snapshot = _snapshot!;
        _busy = true;
        NotifyCommands();
        try
        {
            var result = await _api!.ImportRosterAsync(snapshot.ExamId, snapshot.Version, rows);
            if (context != _context) return;
            if (!result.Applied)
            {
                _pendingPreview = null;
                var original = PreviewRows.ToArray();
                PreviewRows.Clear();
                foreach (var row in original)
                {
                    var issues = result.Issues.Where(x => x.RowNumber == row.RowNumber).Select(x => x.Message).ToArray();
                    PreviewRows.Add(issues.Length == 0 ? row : row with { State = RosterPreviewState.Error, Message = string.Join(' ', issues) });
                }
                Status = "整批未写入：" + string.Join(' ', result.Issues.Select(x => $"第{x.RowNumber}行：{x.Message}"));
                return;
            }
            Apply(result.Snapshot);
            Status = $"服务器已保存：新增 {result.Added} 人，跳过完全重复 {result.Duplicates} 人；本场名册共 {Entries.Count} 人。";
        }
        catch when (context != _context) { }
        catch (Exception error) { HandleWriteError(error); }
        finally { if (context == _context) { _busy = false; NotifyCommands(); } }
    }

    public async Task UpdateSelectedAsync()
    {
        if (!CanEdit || SelectedEntry is null) return;
        var person = _snapshot!.Entries.SingleOrDefault(x => x.AdmissionNo == SelectedEntry.AdmissionNo);
        var input = InputRow();
        if (person is null || input is null) return;
        var snapshot = _snapshot;
        await MutateAsync(() => _api!.UpdateRosterEntryAsync(snapshot.ExamId, person.CandidateId, snapshot.Version, input),
            "考生信息已保存到服务器。已经准备入场或参试的身份由服务器禁止更改。");
    }

    public async Task DeleteSelectedAsync()
    {
        if (!CanEdit || SelectedEntry is null) return;
        var context = _context;
        var person = _snapshot!.Entries.SingleOrDefault(x => x.AdmissionNo == SelectedEntry.AdmissionNo);
        if (person is null || !_confirmDelete($"从本场考试名册删除 {person.Name}（准考证号 {person.AdmissionNo}）？")) return;
        if (context != _context || !CanEdit) return;
        var snapshot = _snapshot!;
        await MutateAsync(() => _api!.DeleteRosterEntryAsync(snapshot.ExamId, person.CandidateId, snapshot.Version), "服务器已删除该考生的未使用名册记录。");
    }

    private async Task MutateAsync(Func<Task<CandidateRosterSnapshot>> action, string success)
    {
        var context = _context;
        _busy = true;
        NotifyCommands();
        try
        {
            var result = await action();
            if (context != _context) return;
            Apply(result);
            Status = success;
        }
        catch when (context != _context) { }
        catch (Exception error) { HandleWriteError(error); }
        finally { if (context == _context) { _busy = false; NotifyCommands(); } }
    }

    private void HandleWriteError(Exception error)
    {
        if (error is AdminApiException { StatusCode: HttpStatusCode.Conflict })
        {
            _snapshot = null;
            InvalidatePreview();
            Status = "服务器名册版本或身份已变化，未覆盖他人的修改。请重新读取并确认。 " + FriendlyErrors.Describe(error);
        }
        else Status = "尚未确认写入结果：" + FriendlyErrors.Describe(error) + " 可在本页重试同一操作，或重新读取服务器核对。";
    }

    private RosterImportRow? InputRow()
    {
        var row = new RosterImportRow(1, Name.Trim(), ClassText.Trim(), AdmissionNo.Trim());
        var errors = RosterWorkbookService.ValidateEntry(row.Name, row.ClassText, row.AdmissionNo);
        if (errors.Count == 0) return row;
        Status = string.Join(' ', errors);
        return null;
    }

    private void Apply(CandidateRosterSnapshot snapshot)
    {
        if (!StringComparer.Ordinal.Equals(snapshot.ExamId, _examId)) throw new InvalidOperationException("不能把其他考试名册应用到当前页面。");
        _snapshot = snapshot;
        Entries.Clear();
        foreach (var row in snapshot.Entries) Entries.Add(new(row.Name, row.ClassText, row.AdmissionNo));
        SelectedEntry = null;
        Name = ClassText = AdmissionNo = string.Empty;
        InvalidatePreview();
        NotifyCommands();
    }

    private void ImportPreview()
    {
        var path = _dialogs.OpenExcelFile();
        if (path is null) return;
        try
        {
            _pendingPreview = _workbooks.ReadPreview(path, Entries);
            PreviewRows.Clear();
            foreach (var row in _pendingPreview.Rows) PreviewRows.Add(row);
            Status = _pendingPreview.HasErrors ? "预览含错误或冲突，修正后重新选择文件；整批不会部分导入。"
                : _api is null ? "文件预览通过；尚未连接服务器和读取考试名册，不能确认导入。"
                : $"预览通过：新增 {_pendingPreview.NewCount} 条，完全重复 {_pendingPreview.DuplicateCount} 条；确认后由服务器再次整体校验并保存。";
        }
        catch (Exception error) { InvalidatePreview(); Status = "读取失败：" + FriendlyErrors.Describe(error); }
        NotifyCommands();
    }

    private void CreateTemplate()
    {
        var path = _dialogs.SaveExcelFile("考生名单模板.xlsx");
        if (path is null) return;
        try { _workbooks.CreateTemplate(path); Status = $"模板已保存：{path}"; }
        catch (Exception error) { Status = "模板保存失败：" + FriendlyErrors.Describe(error); }
    }
    private void InvalidatePreview()
    {
        _pendingPreview = null;
        PreviewRows.Clear();
        ConfirmImportCommand.RaiseCanExecuteChanged();
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(LoadedVersion));
        AddManualCommand?.RaiseCanExecuteChanged();
        UpdateSelectedCommand?.RaiseCanExecuteChanged();
        DeleteSelectedCommand?.RaiseCanExecuteChanged();
        RefreshCommand?.RaiseCanExecuteChanged();
        ImportPreviewCommand?.RaiseCanExecuteChanged();
        ConfirmImportCommand?.RaiseCanExecuteChanged();
    }
    private void OnConnectionChanged(object? sender, EventArgs args) => Reset();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_api is not null) _api.ConfigurationChanged -= OnConnectionChanged;
        Reset();
    }
}
