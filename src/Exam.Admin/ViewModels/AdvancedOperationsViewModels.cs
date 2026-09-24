using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;

namespace Exam.Admin.ViewModels;

public sealed class ImportRevisionViewModel : BindableBase
{
    private const long MaxManifestBytes = 10 * 1024 * 1024;
    private readonly AdminApiClient _api;
    private readonly IManifestFileDialogService _manifestFiles;
    private readonly ExamManifestValidator _manifestValidator = new();
    private string _examId = string.Empty;
    private string _changeReason = string.Empty;
    private string _status = "可查询正式 A06 版本状态；逐题导入、预览与内容发布接口未提供。";
    private string _scheduleVersion = "未查询";
    private string _paperVersion = "未查询";
    private string _gradingVersion = "未查询";
    private string _publicationState = "未查询";
    private bool _isLoading;
    private bool _isPreflighting;
    private bool _staticPreflightPassed;
    private string _preflightStatus = "尚未执行本地清单静态预检；不代表可以编译或发布。";
    private long _contextVersion;
    private long _loadSequence;
    private long _preflightSequence;

    public ImportRevisionViewModel(AdminApiClient api, IManifestFileDialogService? manifestFiles = null)
    {
        _api = api;
        _manifestFiles = manifestFiles ?? new FileDialogService();
        _api.ConfigurationChanged += (_, _) =>
        {
            ResetSnapshot("连接已变更；请重新查询正式版本状态。");
            ResetPreflight("连接已变更；请重新选择并校验清单。");
        };
        LoadVersionCommand = new AsyncRelayCommand(LoadVersionAsync, onError: SetError);
        SelectManifestCommand = new AsyncRelayCommand(SelectManifestAsync, onError: SetPreflightError);
    }

    public string ExamId
    {
        get => _examId;
        set
        {
            if (SetProperty(ref _examId, value))
            {
                ResetSnapshot("考试 ID 已变更；请重新查询正式版本状态。");
                ResetPreflight("考试 ID 已变更；请重新选择并校验清单。");
            }
        }
    }
    public string ChangeReason { get => _changeReason; set => SetProperty(ref _changeReason, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ScheduleVersion { get => _scheduleVersion; private set => SetProperty(ref _scheduleVersion, value); }
    public string PaperVersion { get => _paperVersion; private set => SetProperty(ref _paperVersion, value); }
    public string GradingVersion { get => _gradingVersion; private set => SetProperty(ref _gradingVersion, value); }
    public string PublicationState { get => _publicationState; private set => SetProperty(ref _publicationState, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsPreflighting { get => _isPreflighting; private set => SetProperty(ref _isPreflighting, value); }
    public bool StaticPreflightPassed { get => _staticPreflightPassed; private set => SetProperty(ref _staticPreflightPassed, value); }
    public string PreflightStatus { get => _preflightStatus; private set => SetProperty(ref _preflightStatus, value); }
    public ObservableCollection<ParticipantVersionStatus> ParticipantUpdates { get; } = [];
    public ObservableCollection<ImportIssue> PreflightIssues { get; } = [];
    public AsyncRelayCommand LoadVersionCommand { get; }
    public AsyncRelayCommand SelectManifestCommand { get; }

    public async Task SelectManifestAsync()
    {
        var path = _manifestFiles.OpenManifestFile();
        if (path is null) return;

        var context = _contextVersion;
        var sequence = ++_preflightSequence;
        ClearPreflight();
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            PreflightStatus = "请先填写当前考试 ID，再选择 manifest.json。";
            return;
        }
        if (!string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            PreflightStatus = "只接受名为 manifest.json 的清单；独立答案不在考务端本地预检。";
            return;
        }

        IsPreflighting = true;
        PreflightStatus = "正在读取并静态校验 manifest.json…";
        try
        {
            if (new FileInfo(path).Length > MaxManifestBytes)
                throw new InvalidDataException("清单超过 10 MB 上限。");
            var json = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true));
            if (context != _contextVersion || sequence != _preflightSequence) return;
            if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
                throw new InvalidDataException("清单超过 10 MB 上限。");
            ApplyManifestJson(json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
        {
            if (context == _contextVersion && sequence == _preflightSequence)
                SetPreflightError(exception);
        }
        finally
        {
            if (context == _contextVersion && sequence == _preflightSequence)
                IsPreflighting = false;
        }
    }

    // 合成样包测试走同一入口；这里不接收答案文件，也不将清单内容写入日志或状态。
    public void ValidateManifestJson(string json)
    {
        ++_preflightSequence;
        ClearPreflight();
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            PreflightStatus = "请先填写当前考试 ID。";
            return;
        }
        ApplyManifestJson(json);
    }

    private void ApplyManifestJson(string json)
    {
        var report = _manifestValidator.ValidateJson(json);
        foreach (var issue in report.Issues)
            PreflightIssues.Add(issue);
        if (!report.IsValid)
        {
            PreflightStatus = $"清单静态校验未通过，共 {PreflightIssues.Count} 项问题；未编译、未发布。";
            return;
        }

        // 身份绑定不是第二套清单规则：D01 校验通过后仅核对当前考务页面的考试 ID。
        using var document = JsonDocument.Parse(json);
        var manifestExamId = document.RootElement.GetProperty("examId").GetString();
        if (!string.Equals(manifestExamId, ExamId.Trim(), StringComparison.Ordinal))
        {
            PreflightStatus = "清单考试 ID 与当前考试 ID 不一致；未接受此预检结果。";
            return;
        }

        StaticPreflightPassed = true;
        PreflightStatus = "清单静态校验通过；未在本地校验独立答案，答案关联和逐题满分核对未完成。依赖检查、XeLaTeX 编译、选项定位、发布前计分检查及正式发布仍未完成。";
    }

    private void ResetPreflight(string message)
    {
        ++_preflightSequence;
        ClearPreflight();
        IsPreflighting = false;
        PreflightStatus = message;
    }

    private void ClearPreflight()
    {
        PreflightIssues.Clear();
        StaticPreflightPassed = false;
    }

    private void SetPreflightError(Exception exception)
    {
        ClearPreflight();
        PreflightStatus = exception is InvalidDataException
            ? "清单超过 10 MB 上限；未执行静态预检。"
            : "读取清单失败；请检查文件权限、UTF-8 编码及文件是否仍存在。未执行静态预检。";
    }

    public async Task LoadVersionAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请填写考试 ID。";
            return;
        }

        var examId = ExamId.Trim();
        var context = _contextVersion;
        var sequence = ++_loadSequence;
        IsLoading = true;
        Status = "正在查询服务器版本状态…";
        try
        {
            var snapshot = await _api.GetExamAsync(examId);
            if (context != _contextVersion || sequence != _loadSequence)
            {
                return;
            }
            if (!StringComparer.Ordinal.Equals(snapshot.ExamId, examId))
            {
                throw new InvalidOperationException("服务器返回了不同考试的版本状态，已拒绝使用。");
            }
            ScheduleVersion = snapshot.Versions.ScheduleVersion.ToString();
            PaperVersion = snapshot.Versions.PaperVersion.ToString();
            GradingVersion = snapshot.Versions.GradingVersion.ToString();
            PublicationState = snapshot.Paper is null
                ? "服务器尚无题包发布记录"
                : $"服务器已发布 v{snapshot.Paper.Version}；客户端更新须逐台确认";
            ParticipantUpdates.Clear();
            foreach (var participant in snapshot.Participants.Values)
            {
                ParticipantUpdates.Add(participant);
            }
            Status = $"已读取考试 {snapshot.ExamId} 的正式版本状态；导入任务与草稿发布接口未提供。";
        }
        catch (Exception exception)
        {
            if (context == _contextVersion && sequence == _loadSequence)
            {
                ClearSnapshot();
                SetError(exception);
            }
        }
        finally
        {
            if (context == _contextVersion && sequence == _loadSequence)
            {
                IsLoading = false;
            }
        }
    }

    private void ResetSnapshot(string message)
    {
        _contextVersion++;
        ClearSnapshot();
        IsLoading = false;
        Status = message;
    }

    private void ClearSnapshot()
    {
        ScheduleVersion = "未查询";
        PaperVersion = "未查询";
        GradingVersion = "未查询";
        PublicationState = "未查询";
        ParticipantUpdates.Clear();
    }

    private void SetError(Exception exception) => Status = exception is AdminApiException apiError
        ? $"版本查询失败（{apiError.Code}）：{apiError.Message}"
        : $"版本查询失败：{exception.Message}";
}

public enum ReportAvailability
{
    InterfaceUnavailable,
    NotGenerated,
    ExamNotEnded,
    CollectionIncomplete,
    Recalculating,
    Available
}

public sealed class ReportsWorkspaceViewModel : BindableBase
{
    private string _selectedReport = "学生成绩表";
    private ReportAvailability _availability = ReportAvailability.InterfaceUnavailable;

    public IReadOnlyList<string> ReportTypes { get; } =
    [
        "学生成绩表",
        "各科学生成绩表",
        "各科学生答案表",
        "各科学生小题得分表",
        "班级成绩表"
    ];

    public string SelectedReport { get => _selectedReport; set => SetProperty(ref _selectedReport, value); }
    public ReportAvailability Availability
    {
        get => _availability;
        set
        {
            if (SetProperty(ref _availability, value))
            {
                OnPropertyChanged(nameof(AvailabilityText));
            }
        }
    }
    public string AvailabilityText => Availability switch
    {
        ReportAvailability.NotGenerated => "报告未生成",
        ReportAvailability.ExamNotEnded => "考试未结束",
        ReportAvailability.CollectionIncomplete => "答卷未收齐",
        ReportAvailability.Recalculating => "重算中，旧报告暂不可用",
        ReportAvailability.Available => "报告可用",
        _ => "正式报告接口未提供；以上状态须由服务器返回后才能确定"
    };
}

public sealed class HighAvailabilityWorkspaceViewModel
{
    public string ActiveNode => "接口未提供";
    public string StandbyNode => "接口未提供";
    public string ReplicationState => "接口未提供";
    public string ProtectionMode => "接口未提供";
    public string BackupState => "接口未提供";
    public string IsolationCheck => "接口未提供";
    public string OperationStatus =>
        "当前只有主备 Contract 与核心服务，尚无考务端正式查询、接管、单机应急及重建 API。";
}
