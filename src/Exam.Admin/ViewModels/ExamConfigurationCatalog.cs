using System.Globalization;
using System.IO;
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
/// 考试名称是区分考试的唯一业务依据；内部编号由服务器生成，界面不显示。
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
    private AsyncRelayCommand? _reloadCommand;
    private bool _catalogReady;
    private bool _catalogBusy;
    private string? _requestedExamId;

    public bool CanSave => _api.IsConfigured && _catalogReady && !_catalogBusy;
    public bool CanEditTime => _catalogReady && !_catalogBusy;
    public bool IsLoading => _catalogBusy;
    public AsyncRelayCommand ReloadCommand => _reloadCommand ??= new(
        () => OpenAsync(_requestedExamId!),
        () => _api.IsConfigured && !_catalogBusy && !string.IsNullOrEmpty(_requestedExamId));

    private void ClearLoadedExam()
    {
        _contextVersion++;
        _catalogReady = _catalogBusy = _visibilityBusy = false;
        _requestedExamId = null;
        _examId = string.Empty;
        IsNewExam = false;
        Name = ScheduledStartText = ScheduledEndText = string.Empty;
        _expectedScheduleVersion = null;
        _settingsVersion = 0;
        _visibleToStudents = _visibilityLocked = false;
        Subjects.Clear();
        SelectedSubject = null;
        InstructionsText = ValidationSummary = string.Empty;
        InstructionSourceText = string.Empty;
        _serverInstructionLines.Clear();
        _loadedSettingsFingerprint = string.Empty;
        InstructionPreview.Clear();
        OnPropertyChanged(nameof(ExamId));
        OnPropertyChanged(nameof(ScheduleVersionText));
        NotifyCatalog();
        Status = "连接已更换，请重新选择考试。";
    }

    /// <summary>新建或保存成功后通知主窗口切换到这场考试。</summary>
    public event Action<ExamDetail>? ExamSaved;

    public AsyncRelayCommand SaveCommand => _saveCommand ??= new AsyncRelayCommand(SaveAsync, () => CanSave);

    public bool IsNewExam { get => _isNewExam; private set { if (SetProperty(ref _isNewExam, value)) OnPropertyChanged(nameof(Heading)); } }
    public string Heading => IsNewExam ? "新建考试" : "考试设置";
    public long SettingsVersion => _settingsVersion;

    /// <summary>名称与科目题组只在开考前可改；起止时间按计划规则另行处理。</summary>
    public bool IsSettingsEditable => _lifecycle == ExamLifecycle.Scheduled && !_catalogBusy &&
        (_catalogReady || _requestedExamId is null);

    public bool VisibleToStudents
    {
        get => _visibleToStudents;
        set
        {
            if (_visibleToStudents == value || _visibilityBusy) return;
            _ = ChangeVisibilityAsync(value);
        }
    }

    public bool CanChangeVisibility => CanSave && !IsNewExam && !_visibilityBusy && !(_visibleToStudents && _visibilityLocked);

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
        // 新建考试不得沿用上一场考试的服务器说明。
        _serverInstructionLines.Clear();
        _loadedSettingsFingerprint = string.Empty;
        _catalogReady = true;
        _catalogBusy = _visibilityBusy = false;
        _requestedExamId = null;
        _examId = string.Empty;
        OnPropertyChanged(nameof(ExamId));
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
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        var context = ++_contextVersion;
        // Revoke the previous editor before the first await, including failed loads and A -> B -> A.
        _catalogReady = false;
        _catalogBusy = true;
        _visibilityBusy = false;
        _requestedExamId = examId;
        _examId = string.Empty;
        IsNewExam = false;
        Name = ScheduledStartText = ScheduledEndText = string.Empty;
        _settingsVersion = 0;
        _expectedScheduleVersion = null;
        _visibleToStudents = _visibilityLocked = false;
        Subjects.Clear();
        SelectedSubject = null;
        InstructionsText = ValidationSummary = string.Empty;
        InstructionSourceText = string.Empty;
        _serverInstructionLines.Clear();
        _loadedSettingsFingerprint = string.Empty;
        InstructionPreview.Clear();
        OnPropertyChanged(nameof(ExamId));
        OnPropertyChanged(nameof(ScheduleVersionText));
        NotifyCatalog();
        Status = "正在读取考试设置…";
        try
        {
            var detail = await _api.GetExamDetailAsync(examId);
            if (context != _contextVersion) return;
            if (!StringComparer.Ordinal.Equals(detail.Summary.ExamId, examId))
                throw new InvalidDataException("服务器返回了另一场考试的设置，已拒绝使用。");
            Apply(detail);
            Status = _lifecycle == ExamLifecycle.Scheduled
                ? "已读取考试设置。"
                : "考试已开始或已结束，名称和科目题组只读；起止时间仍可按规则调整。";
        }
        catch when (context != _contextVersion) { }
        catch (Exception error)
        {
            _catalogReady = false;
            Status = $"考试设置读取失败：{FriendlyErrors.Describe(error)} 请重新读取，当前不能保存。";
            throw;
        }
        finally
        {
            if (context == _contextVersion) { _catalogBusy = false; NotifyCatalog(); }
        }
    }

    /// <summary>读取方法自行处理当前上下文的错误，旧请求不能覆盖新页提示。</summary>
    public AsyncRelayCommand OpenCommand(string examId) => new(() => OpenAsync(examId));

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
        _catalogReady = true;
        ScheduleChangeReason = string.Empty;
        _requestedExamId = detail.Summary.ExamId;
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
                    GroupId = group.GroupId, Type = group.Type, Order = group.Order, Name = group.Name,
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
        _serverInstructionLines.Clear();
        // 说明缺失＝服务器还没有这份副本（不是读取失败）：界面会如实说明并改为预览本地填写内容。
        foreach (var line in detail.InstructionLines ?? [])
        {
            if (!string.IsNullOrWhiteSpace(line)) _serverInstructionLines.Add(InstructionLineViewModel.From(line));
        }
        _loadedSettingsFingerprint = SettingsFingerprint();
        NotifyCatalog();
        RefreshInstructions();
    }

    /// <summary>保存考试设置：新建时在服务器创建考试；已有考试保存名称、科目题组，时间变化时另行保存。</summary>
    public async Task SaveAsync()
    {
        if (!CanSave)
        {
            if (!_catalogBusy) Status = "请先成功读取当前考试，或新建考试；当前没有可保存的设置。";
            return;
        }
        RefreshInstructions();
        if (Validate().Count > 0)
        {
            Status = "填写内容有误，请按“校验结果”修改后再保存。";
            return;
        }
        TryReadSchedule(out var start, out var end);
        var context = _contextVersion;
        var examId = ExamId;
        var isNew = IsNewExam;
        var editStructure = _lifecycle == ExamLifecycle.Scheduled;
        var settingsVersion = _settingsVersion;
        var scheduleVersion = _expectedScheduleVersion;
        var name = Name.Trim();
        var settings = ToSettings();
        var schedule = new ScheduleWindow(start, end);
        var timeChanged = ScheduledStartText != _loadedStartText || ScheduledEndText != _loadedEndText;
        var reason = ScheduleChangeReason.Trim();
        if (!isNew && timeChanged && (reason.Length == 0 || reason.Length > 1000))
        {
            Status = "请填写修改时间的原因，长度不超过1000字。";
            return;
        }
        var structureSaved = false;
        var timeSaved = false;
        if (!isNew && timeChanged && scheduleVersion is null)
        {
            Status = "尚未读取当前考试的时间版本，请重新读取后保存。";
            return;
        }
        _catalogBusy = true;
        NotifyCatalog();
        try
        {
            if (isNew)
            {
                Status = "正在新建考试…";
                var created = await _api.CreateExamAsync(name, schedule, settings);
                if (context != _contextVersion) return;
                Apply(created);
                Status = "考试已创建。默认不在学生端显示，可在本页“学生端显示”中打开。";
                ExamSaved?.Invoke(created);
                return;
            }
            if (editStructure)
            {
                var saved = await _api.UpdateExamSettingsAsync(examId, settingsVersion, name, settings);
                if (context != _contextVersion) return;
                if (!StringComparer.Ordinal.Equals(saved.Summary.ExamId, examId))
                    throw new InvalidDataException("设置回执不属于当前考试，请重新读取核对。");
                _settingsVersion = saved.Summary.SettingsVersion;
                structureSaved = true;
            }
            if (timeChanged)
            {
                var saved = await _api.SaveScheduleAsync(examId, schedule, scheduleVersion, reason: reason);
                if (context != _contextVersion) return;
                if (saved.ExamId != examId || saved.Schedule != schedule ||
                    saved.Versions.ScheduleVersion != checked(scheduleVersion!.Value + 1))
                    throw new InvalidDataException("无法确认考试时间保存结果，请重新读取核对。");
                _expectedScheduleVersion = saved.Versions.ScheduleVersion;
                timeSaved = true;
            }
            var detail = await _api.GetExamDetailAsync(examId);
            if (context != _contextVersion) return;
            if (detail.Summary.ExamId != examId)
                throw new InvalidDataException("读取回执不属于当前考试，未替换页面。");
            Apply(detail);
            Status = editStructure ? "考试设置已保存。"
                : timeSaved ? "考试时间已保存；已开考的名称、科目和题组保持不变。"
                : "考试已开始，名称、科目和题组只读；时间没有变化，未提交修改。";
            ExamSaved?.Invoke(detail);
        }
        catch when (context != _contextVersion) { }
        catch (Exception error)
        {
            // A settings write may have committed before a schedule/readback failed. Do not claim all-or-nothing.
            _catalogReady = false;
            var uncertain = error is AdminApiException { Code: "INVALID_RESPONSE" } or HttpRequestException or TaskCanceledException;
            var prefix = structureSaved || timeSaved ? "部分请求已完成，请重新读取核对；"
                : uncertain ? "无法确认保存结果，请重新读取核对；" : "保存未完成；";
            Status = prefix + FriendlyErrors.Describe(error);
            throw;
        }
        finally
        {
            if (context == _contextVersion) { _catalogBusy = false; NotifyCatalog(); }
        }
    }

    private async Task ChangeVisibilityAsync(bool visible)
    {
        if (!CanSave || IsNewExam || string.IsNullOrEmpty(ExamId)) return;
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
            if (context == _contextVersion) { _visibilityBusy = false; NotifyCatalog(); }
        }
    }

    private void NotifyCatalog()
    {
        foreach (var name in new[] { nameof(VisibleToStudents), nameof(CanChangeVisibility), nameof(VisibilityNote),
                     nameof(IsSettingsEditable), nameof(SettingsVersion), nameof(IsNewExam), nameof(Heading),
                     nameof(CanSave), nameof(CanEditTime), nameof(IsLoading) })
            OnPropertyChanged(name);
        _saveCommand?.RaiseCanExecuteChanged();
        _reloadCommand?.RaiseCanExecuteChanged();
        AddSubjectCommand?.RaiseCanExecuteChanged();
        RemoveSubjectCommand?.RaiseCanExecuteChanged();
        AddGroupCommand?.RaiseCanExecuteChanged();
        RemoveGroupCommand?.RaiseCanExecuteChanged();
    }
}
