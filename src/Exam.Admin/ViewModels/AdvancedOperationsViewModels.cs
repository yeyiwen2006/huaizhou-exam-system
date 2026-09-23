using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Versioning;

namespace Exam.Admin.ViewModels;

public sealed class ImportRevisionViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _examId = string.Empty;
    private string _changeReason = string.Empty;
    private string _status = "可查询正式 A06 版本状态；逐题导入、预览与内容发布接口未提供。";
    private string _scheduleVersion = "未查询";
    private string _paperVersion = "未查询";
    private string _gradingVersion = "未查询";
    private string _publicationState = "未查询";
    private bool _isLoading;
    private long _contextVersion;
    private long _loadSequence;

    public ImportRevisionViewModel(AdminApiClient api)
    {
        _api = api;
        _api.ConfigurationChanged += (_, _) => ResetSnapshot("连接已变更；请重新查询正式版本状态。");
        LoadVersionCommand = new AsyncRelayCommand(LoadVersionAsync, onError: SetError);
    }

    public string ExamId
    {
        get => _examId;
        set
        {
            if (SetProperty(ref _examId, value))
            {
                ResetSnapshot("考试 ID 已变更；请重新查询正式版本状态。");
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
    public ObservableCollection<ParticipantVersionStatus> ParticipantUpdates { get; } = [];
    public AsyncRelayCommand LoadVersionCommand { get; }

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
