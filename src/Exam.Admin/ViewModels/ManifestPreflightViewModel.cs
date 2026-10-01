using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Importing;
using Exam.Domain.Importing;

namespace Exam.Admin.ViewModels;

public sealed class ManifestPreflightViewModel : BindableBase
{
    public const long MaxManifestBytes = 10 * 1024 * 1024;

    private readonly IManifestFileDialogService _manifestFiles;
    private readonly IImportSourceFileDialogService _importSources;
    private readonly Func<string, ImportValidationReport> _validateManifest;
    private string _examId = string.Empty;
    private bool _isPreflighting;
    private bool _staticPreflightPassed;
    private ManifestStructurePreview? _structurePreview;
    private string _selectedManifestPath = string.Empty;
    private string _status = "请选择导入源包的 manifest.json，而非 D03 学生包清单；静态预检不代表可以编译、计分、发布或完成客户端更新。";
    private long _contextVersion;
    private long _preflightSequence;

    public ManifestPreflightViewModel(
        AdminApiClient api,
        IManifestFileDialogService? manifestFiles = null)
        : this(api, manifestFiles, json => new ExamManifestValidator().ValidateJson(json))
    {
    }

    public ManifestPreflightViewModel(
        AdminApiClient api,
        IManifestFileDialogService? manifestFiles,
        IImportSourceFileDialogService importSources)
        : this(api, manifestFiles, json => new ExamManifestValidator().ValidateJson(json), importSources)
    {
    }

    internal ManifestPreflightViewModel(
        AdminApiClient api,
        IManifestFileDialogService? manifestFiles,
        Func<string, ImportValidationReport> validateManifest,
        IImportSourceFileDialogService? importSources = null)
    {
        _manifestFiles = manifestFiles ?? new FileDialogService();
        _importSources = importSources ?? new FileDialogService();
        _validateManifest = validateManifest ?? throw new ArgumentNullException(nameof(validateManifest));
        api.ConfigurationChanged += (_, _) =>
            Reset("连接已变更；旧预检结果已清除，请重新选择并校验清单。");
        SelectManifestCommand = new AsyncRelayCommand(SelectManifestAsync, onError: SetPreflightError);
        SelectDirectoryCommand = new AsyncRelayCommand(SelectDirectoryAsync, onError: SetPreflightError);
        SelectZipCommand = new AsyncRelayCommand(SelectZipAsync, onError: SetPreflightError);
    }

    public string ExamId
    {
        get => _examId;
        set
        {
            if (SetProperty(ref _examId, value))
            {
                Reset("考试 ID 已变更；旧预检结果已清除，请重新选择并校验清单。");
            }
        }
    }

    public bool IsPreflighting
    {
        get => _isPreflighting;
        private set => SetProperty(ref _isPreflighting, value);
    }

    public bool StaticPreflightPassed
    {
        get => _staticPreflightPassed;
        private set => SetProperty(ref _staticPreflightPassed, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ObservableCollection<ImportIssue> Issues { get; } = [];
    public ManifestStructurePreview? StructurePreview
    {
        get => _structurePreview;
        private set => SetProperty(ref _structurePreview, value);
    }

    public string SelectedManifestPath
    {
        get => _selectedManifestPath;
        private set => SetProperty(ref _selectedManifestPath, value);
    }

    public AsyncRelayCommand SelectManifestCommand { get; }
    public AsyncRelayCommand SelectDirectoryCommand { get; }
    public AsyncRelayCommand SelectZipCommand { get; }

    public Task SelectManifestAsync() => SelectSourceAsync(
        _manifestFiles.OpenManifestFile, _manifestFiles.ReadManifestAsync, requireManifestName: true);

    public Task SelectDirectoryAsync() => SelectSourceAsync(
        _importSources.OpenImportDirectory, _importSources.ReadManifestFromDirectoryAsync, requireManifestName: false);

    public Task SelectZipAsync() => SelectSourceAsync(
        _importSources.OpenImportZip, _importSources.ReadManifestFromZipAsync, requireManifestName: false);

    private async Task SelectSourceAsync(
        Func<string?> chooseSource,
        Func<string, long, CancellationToken, Task<string>> readManifest,
        bool requireManifestName)
    {
        var context = _contextVersion;
        var sequence = ++_preflightSequence;
        ClearResult();
        IsPreflighting = false;
        SelectedManifestPath = string.Empty;

        string? path;
        try
        {
            path = chooseSource();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetPreflightError(exception);
            return;
        }
        if (context != _contextVersion || sequence != _preflightSequence)
        {
            return;
        }
        if (path is null)
        {
            Status = "已取消选择；旧检查结果和结构预览已清除。";
            return;
        }
        SelectedManifestPath = path;

        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请先填写当前考试 ID（选择并保存考试），再选择试题源。";
            return;
        }

        if (requireManifestName && !string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            Status = "只接受名为 manifest.json 的清单；独立答案不在考务端本地预检范围内。";
            return;
        }

        IsPreflighting = true;
        Status = "正在读取并静态校验 manifest.json…";
        try
        {
            var json = await readManifest(path, MaxManifestBytes, CancellationToken.None);
            if (context != _contextVersion || sequence != _preflightSequence)
            {
                return;
            }

            // D01 的结构扫描和表达式验证可能处理完整 10 MB 清单，不能在 WPF 线程执行。
            var result = await Task.Run(() => InspectManifestJson(json));
            if (context != _contextVersion || sequence != _preflightSequence)
            {
                return;
            }
            ApplyManifestResult(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (context == _contextVersion && sequence == _preflightSequence)
            {
                SetPreflightError(exception);
            }
        }
        finally
        {
            if (context == _contextVersion && sequence == _preflightSequence)
            {
                IsPreflighting = false;
            }
        }
    }

    // 合成样包测试复用正式 D01 校验入口；不接收答案文件，也不记录清单内容。
    public void ValidateManifestJson(string json)
    {
        ++_preflightSequence;
        ClearResult();
        IsPreflighting = false;
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请先填写当前考试 ID。";
            return;
        }

        try
        {
            ApplyManifestResult(InspectManifestJson(json));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetPreflightError(exception);
        }
    }

    private (ImportValidationReport Report, ExamManifest? Manifest) InspectManifestJson(string json)
    {
        var report = _validateManifest(json);
        if (!report.IsValid) return (report, null);
        return (report, JsonSerializer.Deserialize<ExamManifest>(json, ImportJson.Create()));
    }

    private void ApplyManifestResult((ImportValidationReport Report, ExamManifest? Manifest) result)
    {
        var report = result.Report;
        foreach (var issue in report.Issues)
        {
            Issues.Add(issue);
        }

        if (!report.IsValid)
        {
            Status = $"导入源清单静态校验未通过，共 {Issues.Count} 项问题；请勿选择 D03 学生包清单。未编译、未计分、未发布。";
            return;
        }

        if (!string.Equals(result.Manifest?.ExamId, ExamId.Trim(), StringComparison.Ordinal))
        {
            Status = "清单考试 ID 与当前考试 ID 不一致；未接受此预检结果。";
            return;
        }

        StructurePreview = ManifestStructurePreview.FromValidatedManifest(result.Manifest!);
        StaticPreflightPassed = true;
        Status = "清单静态预检通过，可查看本地只读结构；这不等于编译、计分、发布或客户端更新完成。未读取独立答案，声明满分尚未按答案核对。";
    }

    private void Reset(string message)
    {
        _contextVersion++;
        _preflightSequence++;
        ClearResult();
        SelectedManifestPath = string.Empty;
        IsPreflighting = false;
        Status = message;
    }

    private void ClearResult()
    {
        Issues.Clear();
        StaticPreflightPassed = false;
        StructurePreview = null;
    }

    private void SetPreflightError(Exception exception)
    {
        ClearResult();
        Status = exception is InvalidDataException
            ? "导入源格式不正确、根目录缺少唯一 manifest.json，或清单超过 10 MB；未执行静态预检。"
            : "读取清单失败；请检查文件权限、严格 UTF-8 编码及文件是否仍存在。未执行静态预检。";
    }
}
