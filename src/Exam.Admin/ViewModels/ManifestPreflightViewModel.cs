using System.Collections.ObjectModel;
using System.IO;
using System.Text;
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
    private readonly ExamManifestValidator _validator = new();
    private string _examId = string.Empty;
    private bool _isPreflighting;
    private bool _staticPreflightPassed;
    private string _status = "尚未执行本地清单静态预检；这不代表可以编译、计分、发布或完成客户端更新。";
    private long _contextVersion;
    private long _preflightSequence;

    public ManifestPreflightViewModel(
        AdminApiClient api,
        IManifestFileDialogService? manifestFiles = null)
    {
        _manifestFiles = manifestFiles ?? new FileDialogService();
        api.ConfigurationChanged += (_, _) =>
            Reset("连接已变更；旧预检结果已清除，请重新选择并校验清单。");
        SelectManifestCommand = new AsyncRelayCommand(SelectManifestAsync, onError: SetPreflightError);
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
    public AsyncRelayCommand SelectManifestCommand { get; }

    public async Task SelectManifestAsync()
    {
        var path = _manifestFiles.OpenManifestFile();
        if (path is null)
        {
            return;
        }

        var context = _contextVersion;
        var sequence = ++_preflightSequence;
        ClearResult();

        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请先填写当前考试 ID，再选择 manifest.json。";
            return;
        }

        if (!string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            Status = "只接受名为 manifest.json 的清单；独立答案不在考务端本地预检范围内。";
            return;
        }

        IsPreflighting = true;
        Status = "正在读取并静态校验 manifest.json…";
        try
        {
            var json = await _manifestFiles.ReadManifestAsync(path, MaxManifestBytes);
            if (context != _contextVersion || sequence != _preflightSequence)
            {
                return;
            }

            ApplyManifestJson(json);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or DecoderFallbackException)
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
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请先填写当前考试 ID。";
            return;
        }

        ApplyManifestJson(json);
    }

    private void ApplyManifestJson(string json)
    {
        var report = _validator.ValidateJson(json);
        foreach (var issue in report.Issues)
        {
            Issues.Add(issue);
        }

        if (!report.IsValid)
        {
            Status = $"清单静态校验未通过，共 {Issues.Count} 项问题；未编译、未计分、未发布。";
            return;
        }

        using var document = JsonDocument.Parse(json);
        var manifestExamId = document.RootElement.GetProperty("examId").GetString();
        if (!string.Equals(manifestExamId, ExamId.Trim(), StringComparison.Ordinal))
        {
            Status = "清单考试 ID 与当前考试 ID 不一致；未接受此预检结果。";
            return;
        }

        StaticPreflightPassed = true;
        Status = "清单静态预检通过；这不等于编译、计分、发布或客户端更新完成。未读取独立答案，答案关联和逐题满分核对仍未完成。";
    }

    private void Reset(string message)
    {
        _contextVersion++;
        _preflightSequence++;
        ClearResult();
        IsPreflighting = false;
        Status = message;
    }

    private void ClearResult()
    {
        Issues.Clear();
        StaticPreflightPassed = false;
    }

    private void SetPreflightError(Exception exception)
    {
        ClearResult();
        Status = exception is InvalidDataException
            ? "清单超过 10 MB 上限；未执行静态预检。"
            : "读取清单失败；请检查文件权限、严格 UTF-8 编码及文件是否仍存在。未执行静态预检。";
    }
}
