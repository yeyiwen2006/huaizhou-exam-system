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
    private long _visibilityRevision;
    private readonly Dictionary<string, (long Revision, ExamSummary Summary)> _visibilityUpdates = [];
    private readonly Dictionary<string, bool> _pendingVisibility = [];

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
            _visibilityUpdates.Clear();
            _pendingVisibility.Clear();
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
        var visibilityRevision = _visibilityRevision;
        Status = "正在读取考试列表…";
        var exams = await _api.ListExamsAsync();
        if (generation != _generation) return;
        Exams.Clear();
        foreach (var loaded in exams)
        {
            var exam = _visibilityUpdates.TryGetValue(loaded.ExamId, out var visibility) && visibility.Revision > visibilityRevision
                ? visibility.Summary : loaded;
            if (_publicationUpdates.TryGetValue(loaded.ExamId, out var update) && update.Revision > publicationRevision)
                exam = exam with { PaperVersion = update.Version };
            ExamCardViewModel? card = null;
            card = new ExamCardViewModel(exam, () => ExamOpened?.Invoke(card!.Exam), ChangeVisibilityAsync);
            if (_pendingVisibility.TryGetValue(exam.ExamId, out var requested)) card.SetVisibilitySaving(true, requested);
            Exams.Add(card);
        }
        Status = Exams.Count == 0 ? "服务器上还没有考试，请点击“新建考试”。" : $"共 {Exams.Count} 场考试，点击卡片进入。";
    }

    public void UpdatePaperVersion(string examId, long? paperVersion)
    {
        // Preserve updates received while an older list request is still in flight.
        _publicationUpdates[examId] = (++_publicationRevision, paperVersion);
        var card = Exams.FirstOrDefault(item => item.Exam.ExamId == examId);
        if (card is null) return;
        card.ApplySummary(card.Exam with { PaperVersion = paperVersion });
        if (_pendingVisibility.TryGetValue(examId, out var requested)) card.SetVisibilitySaving(true, requested);
    }

    private async Task ChangeVisibilityAsync(ExamCardViewModel card, bool visible)
    {
        var examId = card.Exam.ExamId;
        if (!_api.IsConfigured || _pendingVisibility.ContainsKey(examId)) return;
        var generation = _api.Generation;
        _pendingVisibility[examId] = visible;
        card.SetVisibilitySaving(true);
        Status = $"正在同步“{card.Name}”的学生端显示设置…";
        try
        {
            var summary = await _api.SetExamVisibilityAsync(examId, visible);
            if (generation != _api.Generation) return;
            if (summary.ExamId != examId) throw new InvalidDataException("显示设置回执不属于当前考试。");
            _visibilityUpdates[examId] = (++_visibilityRevision, summary);
            card.ApplySummary(summary);
            foreach (var current in Exams.Where(item => item.Exam.ExamId == examId && !ReferenceEquals(item, card)))
                current.ApplySummary(summary);
            Status = $"“{summary.Name}”已设置为{(summary.VisibleToStudents ? "在学生端显示" : "不在学生端显示")}。";
        }
        catch (Exception error)
        {
            if (generation != _api.Generation) return;
            card.ApplySummary(card.Exam);
            foreach (var current in Exams.Where(item => item.Exam.ExamId == examId && !ReferenceEquals(item, card)))
                current.ApplySummary(current.Exam);
            Status = $"学生端显示设置未确认：{FriendlyErrors.Describe(error)}；请刷新考试列表核对。";
        }
        finally
        {
            if (generation == _api.Generation)
            {
                _pendingVisibility.Remove(examId);
                card.SetVisibilitySaving(false);
                foreach (var current in Exams.Where(item => item.Exam.ExamId == examId)) current.SetVisibilitySaving(false);
            }
        }
    }

    private void SetError(Exception exception) => Status = $"考试列表读取失败：{FriendlyErrors.Describe(exception)}";
}

public sealed class ExamCardViewModel : BindableBase
{
    private readonly Func<ExamCardViewModel, bool, Task>? _changeVisibility;
    private bool _visibleToStudents, _visibilitySaving;
    public ExamCardViewModel(ExamSummary exam, Action open, Func<ExamCardViewModel, bool, Task>? changeVisibility = null)
    {
        Exam = exam;
        _visibleToStudents = exam.VisibleToStudents;
        _changeVisibility = changeVisibility;
        OpenCommand = new RelayCommand(open);
    }
    public ExamSummary Exam { get; private set; }
    public bool VisibleToStudents
    {
        get => _visibleToStudents;
        set
        {
            if (value == _visibleToStudents) return;
            if (!CanChangeVisibility) { OnPropertyChanged(); return; }
            SetProperty(ref _visibleToStudents, value);
            _ = _changeVisibility!(this, value);
        }
    }
    public bool IsVisibilitySaving => _visibilitySaving;
    public bool CanChangeVisibility => _changeVisibility is not null && !_visibilitySaving && !(Exam.VisibleToStudents && Exam.VisibilityLocked);
    public string VisibilityNote => Exam.VisibleToStudents && Exam.VisibilityLocked
        ? "已有考生确认参加考试，全部答卷回收前不能取消显示。" : "勾选或取消后自动同步到服务器。";
    internal void SetVisibilitySaving(bool saving, bool? requested = null)
    {
        _visibilitySaving = saving;
        if (requested.HasValue) _visibleToStudents = requested.Value;
        OnPropertyChanged(nameof(VisibleToStudents));
        OnPropertyChanged(nameof(IsVisibilitySaving));
        OnPropertyChanged(nameof(CanChangeVisibility));
    }
    internal void ApplySummary(ExamSummary summary)
    {
        Exam = summary;
        _visibleToStudents = summary.VisibleToStudents;
        foreach (var property in new[] { nameof(Exam), nameof(Name), nameof(TimeText), nameof(StateText), nameof(StateLevel),
            nameof(VisibleToStudents), nameof(CanChangeVisibility), nameof(VisibilityNote), nameof(PublicationText),
            nameof(PublicationLevel), nameof(ParticipantsText) }) OnPropertyChanged(property);
    }
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
        ? "暂无考生确认参加考试"
        : string.Create(CultureInfo.CurrentCulture, $"实际参加考试 {Exam.ActualParticipants} 人，已回收答卷 {Exam.CollectedSubmissions} 份");
    public RelayCommand OpenCommand { get; }
}
