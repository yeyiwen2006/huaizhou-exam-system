using System.Globalization;
using System.Net.Http;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Exams;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 考试设置与服务器考试目录的衔接：新建考试、读取完整设置、保存名称与科目题组、学生端显示开关。
/// 考试名称即考试类别，是区分考试的唯一凭据；内部编号由服务器生成，界面不显示。
/// </summary>
public sealed partial class ExamConfigurationViewModel
{
    private bool _isNewExam;
    private long _settingsVersion;
    private ExamLifecycle _lifecycle = ExamLifecycle.Scheduled;
    private bool _visibleToStudents;
    private bool _visibilityLocked;
    private bool _visibilityBusy;
    private string _loadedStartText = string.Empty;
    private string _loadedEndText = string.Empty;
    private AsyncRelayCommand? _saveCommand;

    /// <summary>新建或保存成功后通知主窗口切换到这场考试。</summary>
    public event Action<ExamDetail>? ExamSaved;

    public AsyncRelayCommand SaveCommand => _saveCommand ??= new AsyncRelayCommand(SaveAsync, onError: SetError);

    public bool IsNewExam { get => _isNewExam; private set { if (SetProperty(ref _isNewExam, value)) OnPropertyChanged(nameof(Heading)); } }
    public string Heading => IsNewExam ? "新建考试" : "考试设置";
    public long SettingsVersion => _settingsVersion;

    /// <summary>名称与科目题组只在开考前可改；起止时间按计划规则另行处理。</summary>
    public bool IsSettingsEditable => _lifecycle == ExamLifecycle.Scheduled;

    public bool VisibleToStudents
    {
        get => _visibleToStudents;
        set
        {
            if (_visibleToStudents == value || _visibilityBusy) return;
            _ = ChangeVisibilityAsync(value);
        }
    }

    public bool CanChangeVisibility => !IsNewExam && !_visibilityBusy && !(_visibleToStudents && _visibilityLocked);

    public string VisibilityNote => IsNewExam
        ? "考试保存后才能设置是否在学生端显示。"
        : _visibilityLocked
            ? "已有考生确认参试，全部实际参试考生的答卷回收完成前，不能取消在学生端显示。"
            : _visibleToStudents ? "学生登录后可以在选择考试页面看到本场考试。" : "学生端目前看不到本场考试。";

    /// <summary>开始新建：清空为一份可直接修改的默认设置，保存时在服务器创建考试。</summary>
    public void ResetForNew()
    {
        _contextVersion++;
        IsNewExam = true;
        ExamId = string.Empty;
        Name = string.Empty;
        _settingsVersion = 0;
        _lifecycle = ExamLifecycle.Scheduled;
        _visibleToStudents = false;
        _visibilityLocked = false;
        ScheduledStartText = DateTimeOffset.Now.Date.AddDays(1).AddHours(9).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        ScheduledEndText = DateTimeOffset.Now.Date.AddDays(1).AddHours(11).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        _loadedStartText = _loadedEndText = string.Empty;
        Subjects.Clear();
        AddSubject();
        NotifyCatalog();
        RefreshInstructions();
        Status = "请填写考试名称、时间和科目题组，然后点击“保存考试设置”。考试名称不能与已有考试相同。";
    }

    /// <summary>进入一场考试：读取服务器上的完整设置。</summary>
    public async Task OpenAsync(string examId)
    {
        var context = ++_contextVersion;
        Status = "正在读取考试设置…";
        var detail = await _api.GetExamDetailAsync(examId);
        if (context != _contextVersion) return;
        Apply(detail);
        Status = IsSettingsEditable
            ? "已读取考试设置。"
            : "考试已开始或已结束，名称和科目题组不能再修改；起止时间仍可按规则调整。";
    }

    /// <summary>主窗口进入考试时调用；读取失败在本页显示原因。</summary>
    public AsyncRelayCommand OpenCommand(string examId) =>
        new(() => OpenAsync(examId), onError: error => Status = $"考试设置读取失败：{FriendlyErrors.Describe(error)}");

    public ExamSettings ToSettings() => new(Subjects.Select(subject => new ExamSubjectSettings(
        subject.SubjectId.Trim(), subject.Name.Trim(), subject.MaxScore.Trim(),
        subject.Groups.Select(group => new ExamGroupSettings(
            group.GroupId.Trim(), group.Name.Trim(), group.Type, group.Order, group.QuestionCount, group.MaxScore.Trim(),
            group.AllowAbstain,
            group.Type is "single" or "indeterminate" ? new ManifestOneCorrect(group.Correct.Trim(), group.OneBlank.Trim(), group.OneWrong.Trim()) : null,
            group.Type is "multiple" or "indeterminate"
                ? new ManifestMultipleCorrect(group.AllCorrect.Trim(), group.PartialCorrect.Trim(), group.MultipleBlank.Trim(), group.MultipleWrong.Trim())
                : null)).ToArray())).ToArray());

    private void Apply(ExamDetail detail)
    {
        IsNewExam = false;
        _examId = detail.Summary.ExamId;
        OnPropertyChanged(nameof(ExamId));
        Name = detail.Summary.Name;
        _settingsVersion = detail.Summary.SettingsVersion;
        _lifecycle = detail.Summary.Lifecycle;
        _visibleToStudents = detail.Summary.VisibleToStudents;
        _visibilityLocked = detail.Summary.VisibilityLocked;
        ScheduledStartText = _loadedStartText = detail.Summary.StartsAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        ScheduledEndText = _loadedEndText = detail.Summary.EndsAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        _expectedScheduleVersion = detail.ScheduleVersion > 0 ? detail.ScheduleVersion : null;
        OnPropertyChanged(nameof(ScheduleVersionText));
        Subjects.Clear();
        foreach (var subject in detail.Settings.Subjects)
        {
            var draft = new SubjectDraftViewModel(subject.SubjectId, subject.Name, subject.MaxScore);
            foreach (var group in subject.Groups)
            {
                draft.Groups.Add(new GroupDraftViewModel
                {
                    GroupId = group.GroupId, Name = group.Name, Type = group.Type, Order = group.Order,
                    QuestionCount = group.QuestionCount, MaxScore = group.MaxScore, AllowAbstain = group.AllowAbstain,
                    Correct = group.OneCorrect?.Correct ?? "1", OneBlank = group.OneCorrect?.Blank ?? "0", OneWrong = group.OneCorrect?.Wrong ?? "0",
                    AllCorrect = group.MultipleCorrect?.AllCorrect ?? "1", PartialCorrect = group.MultipleCorrect?.PartialCorrect ?? "0",
                    MultipleBlank = group.MultipleCorrect?.Blank ?? "0", MultipleWrong = group.MultipleCorrect?.Wrong ?? "0",
                });
            }
            draft.SelectedGroup = draft.Groups.FirstOrDefault();
            Subjects.Add(draft);
        }
        SelectedSubject = Subjects.FirstOrDefault();
        NotifyCatalog();
        RefreshInstructions();
    }

    /// <summary>保存考试设置：新建时在服务器创建考试；已有考试保存名称、科目题组，时间变化时另行保存。</summary>
    public async Task SaveAsync()
    {
        RefreshInstructions();
        if (Validate().Count > 0)
        {
            Status = "填写内容有误，请按“校验结果”修改后再保存。";
            return;
        }
        TryReadSchedule(out var start, out var end);
        var context = _contextVersion;
        if (IsNewExam)
        {
            Status = "正在新建考试…";
            var created = await _api.CreateExamAsync(Name.Trim(), new ScheduleWindow(start, end), ToSettings());
            if (context != _contextVersion) return;
            Apply(created);
            Status = "考试已创建。默认不在学生端显示，可在本页“学生端显示”中打开。";
            ExamSaved?.Invoke(created);
            return;
        }

        var detail = IsSettingsEditable
            ? await _api.UpdateExamSettingsAsync(ExamId, _settingsVersion, Name.Trim(), ToSettings())
            : null;
        if (context != _contextVersion) return;
        var timeChanged = ScheduledStartText != _loadedStartText || ScheduledEndText != _loadedEndText;
        if (timeChanged)
        {
            await SaveScheduleAsync();
            if (context != _contextVersion) return;
        }
        detail = await _api.GetExamDetailAsync(ExamId);
        if (context != _contextVersion) return;
        Apply(detail);
        Status = IsSettingsEditable || timeChanged ? "考试设置已保存。" : "考试已开始，名称和科目题组不能再修改；时间没有变化。";
        ExamSaved?.Invoke(detail);
    }

    private async Task ChangeVisibilityAsync(bool visible)
    {
        if (IsNewExam || string.IsNullOrEmpty(ExamId)) return;
        var context = _contextVersion;
        _visibilityBusy = true;
        NotifyCatalog();
        try
        {
            var summary = await _api.SetExamVisibilityAsync(ExamId, visible);
            if (context != _contextVersion) return;
            _visibleToStudents = summary.VisibleToStudents;
            _visibilityLocked = summary.VisibilityLocked;
            Status = visible ? "已设置为在学生端显示。" : "已设置为不在学生端显示。";
        }
        catch (Exception error) when (error is AdminApiException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            if (context == _contextVersion) Status = $"显示设置未修改：{FriendlyErrors.Describe(error)}";
        }
        finally
        {
            _visibilityBusy = false;
            NotifyCatalog();
        }
    }

    private void NotifyCatalog()
    {
        foreach (var name in new[] { nameof(VisibleToStudents), nameof(CanChangeVisibility), nameof(VisibilityNote),
                     nameof(IsSettingsEditable), nameof(SettingsVersion), nameof(IsNewExam), nameof(Heading) })
            OnPropertyChanged(name);
    }
}
