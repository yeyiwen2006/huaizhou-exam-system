using System.Collections.ObjectModel;
using System.Globalization;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Desktop.Shared.Controls;

namespace Exam.Admin.ViewModels;

/// <summary>考务端主页：以卡片列出服务器上的全部考试（按名称区分），可进入某场考试或新建考试。</summary>
public sealed partial class ExamHomeViewModel : BindableBase, IDisposable
{
    private readonly AdminApiClient _api;
    private string _status = "连接服务器后显示全部考试。";
    private long _generation;
    private long _publicationRevision;
    private readonly Dictionary<string, (long Revision, long? Version)> _publicationUpdates = [];
    private long _visibilityRevision;
    private readonly Dictionary<string, (long Revision, ExamSummary Summary)> _visibilityUpdates = [];
    private readonly Dictionary<string, bool> _pendingVisibility = [];
    private readonly List<ExamCardViewModel> _cards = [];
    private long _hidingRevision;
    private readonly Dictionary<string, (long Revision, ExamHidingState State)> _hidingUpdates = [];
    private readonly HashSet<string> _pendingHiding = [];

    public ExamHomeViewModel(AdminApiClient api, IExamArchiveService? archives = null, IExamArchiveDirectoryDialog? archiveDirectories = null)
    {
        _api = api;
        _archives = archives ?? new ExamArchiveService(api);
        _archiveDirectories = archiveDirectories ?? new FileDialogService();
        AddExamCommand = new AsyncRelayCommand(AddExamAsync, CanStartArchive);
        CancelArchiveCommand = new RelayCommand(() => _archiveCancellation?.Cancel(), () => IsArchiveBusy && _archiveCancellation is not null);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _api.IsConfigured, SetError);
        NewExamCommand = new RelayCommand(() => NewExamRequested?.Invoke(), () => _api.IsConfigured);
        _api.ConfigurationChanged += OnApiConfigurationChanged;
    }

    private void OnApiConfigurationChanged(object? sender, EventArgs args)
    {
        ResetArchiveSession();
        _generation++;
        Exams.Clear();
        HiddenExams.Clear();
        _cards.Clear();
        _hidingUpdates.Clear();
        _pendingHiding.Clear();
        _publicationUpdates.Clear();
        _visibilityUpdates.Clear();
        _pendingVisibility.Clear();
        Status = "连接服务器后显示全部考试。";
        RefreshCommand.RaiseCanExecuteChanged();
        NewExamCommand.RaiseCanExecuteChanged();
    }

    public ObservableCollection<ExamCardViewModel> Exams { get; } = [];
    public ObservableCollection<ExamCardViewModel> HiddenExams { get; } = [];
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
        var hidingRevision = _hidingRevision;
        Status = "正在读取考试列表…";
        var exams = await _api.ListExamsAsync();
        if (generation != _generation) return;
        _cards.Clear();
        foreach (var loaded in exams)
        {
            var exam = _visibilityUpdates.TryGetValue(loaded.ExamId, out var visibility) && visibility.Revision > visibilityRevision
                ? visibility.Summary : loaded;
            if (_publicationUpdates.TryGetValue(loaded.ExamId, out var update) && update.Revision > publicationRevision)
                exam = exam with { PaperVersion = update.Version };
            if (_hidingUpdates.TryGetValue(loaded.ExamId, out var hiding) && hiding.Revision > hidingRevision)
                exam = WithHiding(exam, hiding.State);
            ExamCardViewModel? card = null;
            card = new ExamCardViewModel(exam, () => ExamOpened?.Invoke(card!.Exam), ChangeVisibilityAsync, ChangeHidingAsync,
                ExportArchiveAsync, () => !_archiveDisposed && _api.IsConfigured && !IsArchiveBusy);
            if (_pendingVisibility.TryGetValue(exam.ExamId, out var requested)) card.SetVisibilitySaving(true, requested);
            if (_pendingHiding.Contains(exam.ExamId)) card.SetHidingSaving(true);
            _cards.Add(card);
        }
        RebuildLists();
        Status = _cards.Count == 0 ? "服务器上还没有考试，请点击“添加考试”。" : $"共 {Exams.Count} 场显示的考试，{HiddenExams.Count} 场隐藏的考试，点击卡片进入。";
    }

    public void UpdatePaperVersion(string examId, long? paperVersion)
    {
        // Preserve updates received while an older list request is still in flight.
        _publicationUpdates[examId] = (++_publicationRevision, paperVersion);
        var card = _cards.FirstOrDefault(item => item.Exam.ExamId == examId);
        if (card is null) return;
        card.ApplySummary(card.Exam with { PaperVersion = paperVersion });
        if (_pendingVisibility.TryGetValue(examId, out var requested)) card.SetVisibilitySaving(true, requested);
    }

    private async Task ChangeVisibilityAsync(ExamCardViewModel card, bool visible)
    {
        var examId = card.Exam.ExamId;
        if (!_api.IsConfigured || _pendingVisibility.ContainsKey(examId)) return;
        var generation = _api.Generation;
        var publicationRevision = _publicationRevision;
        var hidingRevision = _hidingRevision;
        _pendingVisibility[examId] = visible;
        card.SetVisibilitySaving(true);
        Status = $"正在同步“{card.Name}”的学生端显示设置…";
        try
        {
            var summary = await _api.SetExamVisibilityAsync(examId, visible);
            if (generation != _api.Generation) return;
            if (summary.ExamId != examId) throw new InvalidDataException("显示设置回执不属于当前考试。");
            // A visibility response may have been read before a newer publication event.
            // Preserve that event (including an unknown result) rather than rolling it back.
            if (_publicationUpdates.TryGetValue(examId, out var update) && update.Revision > publicationRevision)
                summary = summary with { PaperVersion = update.Version };
            if (_hidingUpdates.TryGetValue(examId, out var hiding) && hiding.Revision > hidingRevision)
                summary = WithHiding(summary, hiding.State);
            _visibilityUpdates[examId] = (++_visibilityRevision, summary);
            card.ApplySummary(summary);
            foreach (var current in _cards.Where(item => item.Exam.ExamId == examId && !ReferenceEquals(item, card)))
                current.ApplySummary(summary);
            RebuildLists();
            Status = $"“{summary.Name}”已设置为{(summary.VisibleToStudents ? "在学生端显示" : "不在学生端显示")}。";
        }
        catch (Exception error)
        {
            if (generation != _api.Generation) return;
            card.ApplySummary(card.Exam);
            foreach (var current in _cards.Where(item => item.Exam.ExamId == examId && !ReferenceEquals(item, card)))
                current.ApplySummary(current.Exam);
            Status = $"学生端显示设置未确认：{FriendlyErrors.Describe(error)}；请刷新考试列表核对。";
        }
        finally
        {
            if (generation == _api.Generation)
            {
                _pendingVisibility.Remove(examId);
                card.SetVisibilitySaving(false);
                foreach (var current in _cards.Where(item => item.Exam.ExamId == examId)) current.SetVisibilitySaving(false);
            }
        }
    }

    private async Task ChangeHidingAsync(ExamCardViewModel card, ExamHidingScope scope)
    {
        var examId = card.Exam.ExamId;
        if (!_api.IsConfigured || !_pendingHiding.Add(examId)) return;
        var generation = _api.Generation;
        var hidden = !card.Exam.IsHidden;
        card.SetHidingSaving(true);
        Status = $"正在{(hidden ? "隐藏" : "取消隐藏")}“{card.Name}”…";
        try
        {
            var state = await _api.SetExamHidingAsync(examId, hidden, scope);
            if (generation != _api.Generation) return;
            if (state.ExamId != examId) throw new InvalidDataException("隐藏设置回执不属于当前考试。");
            _hidingUpdates[examId] = (++_hidingRevision, state);
            card.ApplySummary(WithHiding(card.Exam, state));
            foreach (var current in _cards.Where(item => item.Exam.ExamId == examId && !ReferenceEquals(item, card)))
                current.ApplySummary(WithHiding(current.Exam, state));
            RebuildLists();
            var scopeText = scope == ExamHidingScope.CurrentOperator ? "当前账号" : "所有考务账号";
            Status = $"“{card.Name}”已{(hidden ? "隐藏" : "取消隐藏")}（{scopeText}）。";
            if (!hidden && card.Exam.IsHidden) Status += "另一范围的隐藏仍生效，可继续取消。";
        }
        catch (Exception error)
        {
            if (generation == _api.Generation)
                Status = $"隐藏设置未确认：{FriendlyErrors.Describe(error)}；请刷新考试列表核对。";
        }
        finally
        {
            if (generation == _api.Generation)
            {
                _pendingHiding.Remove(examId);
                card.SetHidingSaving(false);
                foreach (var current in _cards.Where(item => item.Exam.ExamId == examId)) current.SetHidingSaving(false);
            }
        }
    }

    private static ExamSummary WithHiding(ExamSummary summary, ExamHidingState state) => summary with
    {
        HiddenForCurrentOperator = state.HiddenForCurrentOperator,
        HiddenForAllOperators = state.HiddenForAllOperators
    };

    private void RebuildLists()
    {
        Exams.Clear();
        HiddenExams.Clear();
        foreach (var card in _cards)
            (card.Exam.IsHidden ? HiddenExams : Exams).Add(card);
    }

    private void SetError(Exception exception) => Status = $"考试列表读取失败：{FriendlyErrors.Describe(exception)}";
}

public sealed class ExamCardViewModel : BindableBase
{
    private readonly Func<ExamCardViewModel, bool, Task>? _changeVisibility;
    private readonly Func<ExamCardViewModel, ExamHidingScope, Task>? _changeHiding;
    private bool _visibleToStudents, _visibilitySaving, _hidingSaving;
    public ExamCardViewModel(ExamSummary exam, Action open, Func<ExamCardViewModel, bool, Task>? changeVisibility = null,
        Func<ExamCardViewModel, ExamHidingScope, Task>? changeHiding = null,
        Func<ExamCardViewModel, Task>? export = null, Func<bool>? canExport = null)
    {
        Exam = exam;
        _visibleToStudents = exam.VisibleToStudents;
        _changeVisibility = changeVisibility;
        _changeHiding = changeHiding;
        OpenCommand = new RelayCommand(open);
        ExportArchiveCommand = new AsyncRelayCommand(() => export!(this), () => export is not null && (canExport?.Invoke() ?? true));
        ChangePersonalHidingCommand = new AsyncRelayCommand(() => _changeHiding!(this, ExamHidingScope.CurrentOperator),
            () => CanChangeHiding && (!Exam.IsHidden || Exam.HiddenForCurrentOperator));
        ChangeSharedHidingCommand = new AsyncRelayCommand(() => _changeHiding!(this, ExamHidingScope.AllOperators),
            () => CanChangeHiding && (!Exam.IsHidden || Exam.HiddenForAllOperators));
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
    public bool CanChangeVisibility => _changeVisibility is not null && !_visibilitySaving && !_hidingSaving && !(Exam.VisibleToStudents && Exam.Lifecycle == ExamLifecycle.Running);
    public bool IsHidingSaving => _hidingSaving;
    public bool CanChangeHiding => _changeHiding is not null && !_visibilitySaving && !_hidingSaving;
    public string HidingActionText => Exam.IsHidden ? "取消隐藏" : "隐藏该考试";
    public string HidingScopeText => (Exam.HiddenForCurrentOperator, Exam.HiddenForAllOperators) switch
    {
        (true, true) => "隐藏范围：当前账号及所有考务账号",
        (true, false) => "隐藏范围：当前账号",
        (false, true) => "隐藏范围：所有考务账号",
        _ => string.Empty
    };
    public AsyncRelayCommand ChangePersonalHidingCommand { get; }
    public AsyncRelayCommand ChangeSharedHidingCommand { get; }
    public AsyncRelayCommand ExportArchiveCommand { get; }
    internal void SetHidingSaving(bool saving)
    {
        _hidingSaving = saving;
        OnPropertyChanged(nameof(IsHidingSaving));
        OnPropertyChanged(nameof(CanChangeVisibility));
        NotifyHidingChanged();
    }
    private void NotifyHidingChanged()
    {
        OnPropertyChanged(nameof(CanChangeHiding));
        OnPropertyChanged(nameof(HidingActionText));
        OnPropertyChanged(nameof(HidingScopeText));
        ChangePersonalHidingCommand.RaiseCanExecuteChanged();
        ChangeSharedHidingCommand.RaiseCanExecuteChanged();
    }
    public string VisibilityNote => Exam.VisibleToStudents && Exam.Lifecycle == ExamLifecycle.Running
        ? "考试进行中不能取消显示；考试结束后可以取消显示。" : "勾选或取消后自动同步到服务器。";
    internal void SetVisibilitySaving(bool saving, bool? requested = null)
    {
        _visibilitySaving = saving;
        if (requested.HasValue) _visibleToStudents = requested.Value;
        OnPropertyChanged(nameof(VisibleToStudents));
        OnPropertyChanged(nameof(IsVisibilitySaving));
        OnPropertyChanged(nameof(CanChangeVisibility));
        NotifyHidingChanged();
    }
    internal void ApplySummary(ExamSummary summary)
    {
        Exam = summary;
        _visibleToStudents = summary.VisibleToStudents;
        foreach (var property in new[] { nameof(Exam), nameof(Name), nameof(TimeText), nameof(StateText), nameof(StateLevel),
            nameof(VisibleToStudents), nameof(CanChangeVisibility), nameof(VisibilityNote), nameof(PublicationText),
            nameof(PublicationLevel), nameof(ParticipantsText) }) OnPropertyChanged(property);
        NotifyHidingChanged();
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
