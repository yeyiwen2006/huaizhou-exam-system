using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;

namespace Exam.Admin.ViewModels;

public sealed class RosterViewModel : BindableBase
{
    private readonly RosterWorkbookService _workbooks;
    private readonly IFileDialogService _dialogs;
    private string _name = string.Empty;
    private string _classText = string.Empty;
    private string _admissionNo = string.Empty;
    private string _status = "名册仅在当前进程内存中，关闭程序即丢失；服务器尚未提供正式名册写入 API。";
    private RosterPreview? _pendingPreview;

    public RosterViewModel(RosterWorkbookService workbooks, IFileDialogService dialogs)
    {
        _workbooks = workbooks;
        _dialogs = dialogs;
        AddManualCommand = new RelayCommand(AddManual);
        CreateTemplateCommand = new RelayCommand(CreateTemplate);
        ImportPreviewCommand = new RelayCommand(ImportPreview);
        ConfirmImportCommand = new RelayCommand(ConfirmImport, () => _pendingPreview is { HasErrors: false, NewCount: > 0 });
        Entries.CollectionChanged += (_, _) => InvalidatePreview();
    }

    public ObservableCollection<RosterEntryDraft> Entries { get; } = [];
    public ObservableCollection<RosterPreviewRow> PreviewRows { get; } = [];

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string ClassText { get => _classText; set => SetProperty(ref _classText, value); }
    public string AdmissionNo { get => _admissionNo; set => SetProperty(ref _admissionNo, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public RelayCommand AddManualCommand { get; }
    public RelayCommand CreateTemplateCommand { get; }
    public RelayCommand ImportPreviewCommand { get; }
    public RelayCommand ConfirmImportCommand { get; }

    private void InvalidatePreview()
    {
        _pendingPreview = null;
        PreviewRows.Clear();
        ConfirmImportCommand.RaiseCanExecuteChanged();
    }

    private void AddManual()
    {
        var entry = new RosterEntryDraft(Name.Trim(), ClassText.Trim(), AdmissionNo.Trim());
        var errors = RosterWorkbookService.ValidateEntry(entry.Name, entry.ClassText, entry.AdmissionNo);
        if (errors.Count > 0)
        {
            Status = string.Join(' ', errors);
            return;
        }

        var prior = Entries.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.AdmissionNo, entry.AdmissionNo));
        if (prior is not null)
        {
            Status = prior == entry ? "该名册记录已经存在。" : "准考证号与已有记录冲突，未写入。";
            return;
        }

        Entries.Add(entry);
        Name = string.Empty;
        ClassText = string.Empty;
        AdmissionNo = string.Empty;
        Status = $"已加入本地名册，共 {Entries.Count} 人；若先前有导入预览，现已失效，需重新选择文件。";
    }

    private void CreateTemplate()
    {
        var path = _dialogs.SaveExcelFile("考生名册模板.xlsx");
        if (path is null)
        {
            return;
        }

        try
        {
            _workbooks.CreateTemplate(path);
            Status = $"模板已保存：{path}";
        }
        catch (Exception exception)
        {
            Status = $"模板保存失败：{exception.Message}";
        }
    }

    private void ImportPreview()
    {
        var path = _dialogs.OpenExcelFile();
        if (path is null)
        {
            return;
        }

        try
        {
            _pendingPreview = _workbooks.ReadPreview(path, Entries);
            PreviewRows.Clear();
            foreach (var row in _pendingPreview.Rows)
            {
                PreviewRows.Add(row);
            }

            ConfirmImportCommand.RaiseCanExecuteChanged();
            Status = _pendingPreview.HasErrors
                ? "预览含错误或冲突，必须全部修正后重新选择文件；本次不能部分导入。"
                : $"预览通过：新增 {_pendingPreview.NewCount} 条，完全重复 {_pendingPreview.DuplicateCount} 条。请确认后整体导入。";
        }
        catch (Exception exception)
        {
            _pendingPreview = null;
            PreviewRows.Clear();
            ConfirmImportCommand.RaiseCanExecuteChanged();
            Status = $"读取失败：{exception.Message}";
        }
    }

    private void ConfirmImport()
    {
        if (_pendingPreview is not { HasErrors: false } preview)
        {
            Status = "没有可确认的有效预览。";
            return;
        }

        InvalidatePreview();
        foreach (var row in preview.Rows.Where(row => row.State == RosterPreviewState.New))
        {
            Entries.Add(new RosterEntryDraft(row.Name, row.ClassText, row.AdmissionNo));
        }

        var added = preview.NewCount;
        _pendingPreview = null;
        PreviewRows.Clear();
        ConfirmImportCommand.RaiseCanExecuteChanged();
        Status = $"已整体加入本地名册 {added} 条，共 {Entries.Count} 人。仅本进程内存有效，关闭即丢失，尚未写入服务器。";
    }
}
