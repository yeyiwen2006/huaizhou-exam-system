using System.Collections.ObjectModel;
using System.Globalization;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Desktop.Shared.Controls;

namespace Exam.Admin.ViewModels;

/// <summary>考务端主页：以卡片列出服务器上的全部考试（按名称区分），可进入某场考试或新建考试。</summary>
public sealed class ExamHomeViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _status = "连接服务器后显示全部考试。";
    private long _generation;
    private long _publicationRevision;
    private readonly Dictionary<string, (long Revision, long? Version)> _publicationUpdates = [];

    public ExamHomeViewModel(AdminApiClient api)
    {
        _api = api;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _api.IsConfigured, SetError);
        NewExamCommand = new RelayCommand(() => NewExamRequested?.Invoke(), () => _api.IsConfigured);
        _api.ConfigurationChanged += (_, _) =>
        {
            _generation++;
            Exams.Clear();
            _publicationUpdates.Clear();
            Status = "连接服务器后显示全部考试。";
            RefreshCommand.RaiseCanExecuteChanged();
            NewExamCommand.RaiseCanExecuteChanged();
        };
    }

    public ObservableCollection<ExamCardViewModel> Exams { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand NewExamCommand { get; }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public event Action<ExamSummary>? ExamOpened;
    public event Action? NewExamRequested;

    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        var publicationRevision = _publicationRevision;
        Status = "正在读取考试列表…";
        var exams = await _api.ListExamsAsync();
        if (generation != _generation) return;
        Exams.Clear();
        foreach (var loaded in exams)
        {
            var exam = _publicationUpdates.TryGetValue(loaded.ExamId, out var update) && update.Revision > publicationRevision
                ? loaded with { PaperVersion = update.Version } : loaded;
            Exams.Add(new ExamCardViewModel(exam, () => ExamOpened?.Invoke(exam)));
        }
        Status = Exams.Count == 0 ? "服务器上还没有考试，请点击“新建考试”。" : $"共 {Exams.Count} 场考试，点击卡片进入。";
    }

    public void UpdatePaperVersion(string examId, long? paperVersion)
    {
        // Preserve updates received while an older list request is still in flight.
        _publicationUpdates[examId] = (++_publicationRevision, paperVersion);
        var card = Exams.FirstOrDefault(item => item.Exam.ExamId == examId);
        if (card is null) return;
        var exam = card.Exam with { PaperVersion = paperVersion };
        Exams[Exams.IndexOf(card)] = new ExamCardViewModel(exam, () => ExamOpened?.Invoke(exam));
    }

    private void SetError(Exception exception) => Status = $"考试列表读取失败：{FriendlyErrors.Describe(exception)}";
}

public sealed class ExamCardViewModel(ExamSummary exam, Action open)
{
    public ExamSummary Exam { get; } = exam;
    public string Name => Exam.Name;
    public string TimeText => Exam.StartsAt.Date == Exam.EndsAt.Date
        ? $"{Exam.StartsAt.ToLocalTime():yyyy-MM-dd HH:mm} — {Exam.EndsAt.ToLocalTime():HH:mm}"
        : $"{Exam.StartsAt.ToLocalTime():yyyy-MM-dd HH:mm} — {Exam.EndsAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    public string StateText => Exam.Lifecycle switch
    {
        ExamLifecycle.Running => "进行中",
        ExamLifecycle.Ended => "已结束",
        _ => "未开始",
    };
    public StatusLevel StateLevel => Exam.Lifecycle switch
    {
        ExamLifecycle.Running => StatusLevel.Success,
        ExamLifecycle.Ended => StatusLevel.Neutral,
        _ => StatusLevel.Info,
    };
    public string VisibilityText => Exam.VisibleToStudents ? "学生端可见" : "学生端不显示";
    public StatusLevel VisibilityLevel => Exam.VisibleToStudents ? StatusLevel.Success : StatusLevel.Neutral;
    public string PublicationText => Exam.PaperVersion switch
    {
        > 0 => "试卷已发布",
        0 => "试卷未发布",
        _ => "发布状态未确认",
    };
    public StatusLevel PublicationLevel => Exam.PaperVersion switch
    {
        > 0 => StatusLevel.Success,
        0 => StatusLevel.Neutral,
        _ => StatusLevel.Warning,
    };
    public string ParticipantsText => Exam.ActualParticipants == 0
        ? "暂无考生确认参试"
        : string.Create(CultureInfo.CurrentCulture, $"实际参试 {Exam.ActualParticipants} 人，已回收答卷 {Exam.CollectedSubmissions} 份");
    public RelayCommand OpenCommand { get; } = new(open);
}
