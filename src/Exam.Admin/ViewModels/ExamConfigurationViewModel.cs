using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Versioning;
using Exam.Domain.Exams;

namespace Exam.Admin.ViewModels;

public sealed partial class ExamConfigurationViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _examId = "exam-draft";
    private string _name = "新考试";
    private string _scheduledStartText = DateTimeOffset.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    private string _scheduledEndText = DateTimeOffset.Now.AddHours(3).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    private SubjectDraftViewModel? _selectedSubject;
    private string _status = "请从“全部考试”进入一场考试，或新建考试。";
    private string _validationSummary = string.Empty;
    private string _instructionsText = string.Empty;
    private string _instructionSourceText = string.Empty;
    private readonly List<InstructionLineViewModel> _serverInstructionLines = [];
    private string _loadedSettingsFingerprint = string.Empty;
    private string _scheduleChangeReason = string.Empty;
    private long? _expectedScheduleVersion;
    private long _contextVersion;
    private long _editVersion;
    private long _scheduleOperation;

    /// <summary>已读取或已保存的服务器考试时间版本；用于保存时的并发核对，不在界面显示。</summary>
    public long? LoadedScheduleVersion => _expectedScheduleVersion;

    public string ScheduleVersionText => _expectedScheduleVersion is { } version
        ? $"已读取服务器上的考试时间。保存时如果其他人已修改过，会提示重新读取，不会覆盖。"
        : "尚未读取服务器上的考试时间；请先创建考试或读取已有考试，再修改时间。";

    private void ResetScheduleContext()
    {
        _contextVersion++;
        ScheduleChangeReason = string.Empty;
        _catalogReady = false;
        _requestedExamId = string.IsNullOrEmpty(_examId) ? null : _examId;
        _expectedScheduleVersion = null;
        NotifyCatalog();
        OnPropertyChanged(nameof(ScheduleVersionText));
    }

    public ExamConfigurationViewModel(AdminApiClient api)
    {
        _api = api;
        _api.ConfigurationChanged += (_, _) => ClearLoadedExam();
        AddSubjectCommand = new RelayCommand(AddSubject, () => IsSettingsEditable);
        RemoveSubjectCommand = new RelayCommand(RemoveSubject, () => IsSettingsEditable && SelectedSubject is not null);
        AddGroupCommand = new RelayCommand(AddGroup, () => IsSettingsEditable && SelectedSubject is not null);
        RemoveGroupCommand = new RelayCommand(RemoveGroup, () => IsSettingsEditable && SelectedSubject?.SelectedGroup is not null);
        RefreshInstructionsCommand = new RelayCommand(RefreshInstructions);
        SaveScheduleCommand = new AsyncRelayCommand(SaveScheduleAsync, onError: SetError);
        LoadScheduleCommand = new AsyncRelayCommand(LoadScheduleAsync, onError: SetError);
        AddSubject();
        RefreshInstructions();
    }

    public ObservableCollection<SubjectDraftViewModel> Subjects { get; } = [];

    /// <summary>考生说明预览：正文黑体、数字 Times New Roman；内容来自服务器已保存的说明或当前填写内容。</summary>
    public ObservableCollection<InstructionLineViewModel> InstructionPreview { get; } = [];
    /// <summary>题型下拉项：保存值沿用计分规则中的英文标识，界面显示中文名称。</summary>
    public IReadOnlyList<QuestionTypeOption> QuestionTypes { get; } =
    [
        new("single", "单项选择题"),
        new("multiple", "多项选择题"),
        new("indeterminate", "不定项选择题"),
    ];

    public string ExamId
    {
        get => _examId;
        set { if (SetProperty(ref _examId, value)) ResetScheduleContext(); }
    }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string ScheduleChangeReason { get => _scheduleChangeReason; set { if (SetProperty(ref _scheduleChangeReason, value)) _editVersion++; } }
    public string ScheduledStartText { get => _scheduledStartText; set { if (SetProperty(ref _scheduledStartText, value)) _editVersion++; } }
    public string ScheduledEndText { get => _scheduledEndText; set { if (SetProperty(ref _scheduledEndText, value)) _editVersion++; } }

    public SubjectDraftViewModel? SelectedSubject
    {
        get => _selectedSubject;
        set
        {
            if (_selectedSubject is not null) _selectedSubject.PropertyChanged -= SelectedSubjectChanged;
            if (value is not null) value.PropertyChanged += SelectedSubjectChanged;
            if (SetProperty(ref _selectedSubject, value))
            {
                RemoveSubjectCommand.RaiseCanExecuteChanged();
                AddGroupCommand.RaiseCanExecuteChanged();
                RemoveGroupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ValidationSummary { get => _validationSummary; private set => SetProperty(ref _validationSummary, value); }
    public string InstructionsText { get => _instructionsText; private set => SetProperty(ref _instructionsText, value); }

    /// <summary>预览来源说明：告诉考务这一份是服务器保存的（学生端所见）还是尚未保存的填写内容。</summary>
    public string InstructionSourceText { get => _instructionSourceText; private set => SetProperty(ref _instructionSourceText, value); }

    public RelayCommand AddSubjectCommand { get; }
    public RelayCommand RemoveSubjectCommand { get; }
    public RelayCommand AddGroupCommand { get; }
    public RelayCommand RemoveGroupCommand { get; }
    public RelayCommand RefreshInstructionsCommand { get; }
    public AsyncRelayCommand SaveScheduleCommand { get; }
    public AsyncRelayCommand LoadScheduleCommand { get; }

    private void SelectedSubjectChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SubjectDraftViewModel.SelectedGroup))
            RemoveGroupCommand.RaiseCanExecuteChanged();
    }

    public async Task LoadScheduleAsync()
    {
        var examId = ExamId.Trim();
        if (examId.Length == 0) { Status = "请填写考试编号。"; return; }
        var context = _contextVersion;
        var edits = _editVersion;
        var operation = ++_scheduleOperation;
        var snapshot = await _api.GetExamAsync(examId);
        if (context != _contextVersion || edits != _editVersion || operation != _scheduleOperation) return;
        if (!StringComparer.Ordinal.Equals(snapshot.ExamId, examId))
            throw new InvalidOperationException("服务器返回的不是这场考试的数据，已忽略，请重试。");
        ScheduledStartText = snapshot.Schedule.StartsAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        ScheduledEndText = snapshot.Schedule.EndsAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        _expectedScheduleVersion = snapshot.Versions.ScheduleVersion;
        OnPropertyChanged(nameof(ScheduleVersionText));
        Status = "已读取服务器上的考试时间；完整考试设置请使用本页重新读取功能。";
    }

    /// <summary>与服务器保存时相同的规则（ExamSettingsRules），另加本机时间格式检查。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(ExamSettingsRules.Validate(Name, ToSettings()));
        if (!TryReadSchedule(out var start, out var end))
        {
            errors.Add("请按“年-月-日 时:分”填写开始和结束时间，且结束时间要晚于开始时间。");
        }
        else if (end <= start)
        {
            errors.Add("结束时间必须晚于开始时间。");
        }
        return errors;
    }

    private void AddSubject()
    {
        var number = 1;
        while (Subjects.Any(subject => StringComparer.Ordinal.Equals(subject.SubjectId, $"subject-{number}")))
        {
            number++;
        }
        var subject = new SubjectDraftViewModel($"subject-{number}", $"科目{number}", "0");
        Subjects.Add(subject);
        SelectedSubject = subject;
        AddGroup();
    }

    private void RemoveSubject()
    {
        if (SelectedSubject is null)
        {
            return;
        }
        var index = Subjects.IndexOf(SelectedSubject);
        Subjects.Remove(SelectedSubject);
        SelectedSubject = Subjects.Count == 0 ? null : Subjects[Math.Clamp(index, 0, Subjects.Count - 1)];
        RefreshInstructions();
    }

    private void AddGroup()
    {
        if (SelectedSubject is null)
        {
            return;
        }
        var number = 1;
        while (SelectedSubject.Groups.Any(group =>
                   StringComparer.Ordinal.Equals(group.GroupId, $"{SelectedSubject.SubjectId}-group-{number}") ||
                   group.Order == number))
        {
            number++;
        }
        var group = new GroupDraftViewModel
        {
            GroupId = $"{SelectedSubject.SubjectId}-group-{number}",
            Type = "single",
            Order = number,
            QuestionCount = 0,
            MaxScore = "0"
        };
        SelectedSubject.Groups.Add(group);
        SelectedSubject.SelectedGroup = group;
        RemoveGroupCommand.RaiseCanExecuteChanged();
    }

    private void RemoveGroup()
    {
        if (SelectedSubject?.SelectedGroup is not { } group)
        {
            return;
        }
        SelectedSubject.Groups.Remove(group);
        SelectedSubject.SelectedGroup = SelectedSubject.Groups.FirstOrDefault();
        RemoveGroupCommand.RaiseCanExecuteChanged();
        RefreshInstructions();
    }

    public async Task SaveScheduleAsync()
    {
        RefreshInstructions();
        var errors = Validate();
        if (errors.Count > 0)
        {
            Status = "填写内容有误，请按下方“校验结果”修改后再保存。";
            return;
        }

        TryReadSchedule(out var start, out var end);
        Status = "正在保存考试时间…";
        var examId = ExamId.Trim();
        var context = _contextVersion;
        var edits = _editVersion;
        var operation = ++_scheduleOperation;
        var expectedVersion = _expectedScheduleVersion;
        // A conditional schedule update increments the loaded version exactly once.
        // Capture before awaiting: a positive but stale/unrelated version is not a save receipt.
        var savedVersion = expectedVersion is { } version ? checked(version + 1) : 1;
        var schedule = new ScheduleWindow(start, end);
        var snapshot = await _api.SaveScheduleAsync(examId, schedule, expectedVersion, reason: ScheduleChangeReason);
        if (context != _contextVersion || operation != _scheduleOperation) return;
        if (!StringComparer.Ordinal.Equals(snapshot.ExamId, examId) ||
            snapshot.Schedule != schedule || snapshot.Versions is null ||
            snapshot.Versions.ScheduleVersion != savedVersion)
            throw new InvalidOperationException("无法确认保存结果，请点击“读取服务器上的时间”核对后再操作。");
        _expectedScheduleVersion = snapshot.Versions.ScheduleVersion;
        OnPropertyChanged(nameof(ScheduleVersionText));
        Status = edits == _editVersion
            ? "考试时间已保存。"
            : $"此前填写的考试时间已保存；保存期间修改的时间尚未保存，请核对后再次保存。";
    }

    private void RefreshInstructions()
    {
        var errors = Validate();
        ValidationSummary = errors.Count == 0
            ? "填写格式检查通过。计分规则能否算出正确的满分，要在导入试题和答案时再核对。"
            : string.Join(Environment.NewLine, errors.Select(error => $"• {error}"));
        InstructionsText = BuildInstructions();
        UpdateInstructionPreview();
    }

    /// <summary>
    /// 选择预览来源：没有未保存的设置更改时回显服务器保存的说明（学生端当前显示的那一份），
    /// 有更改时预览当前填写内容，并说明两者是否一致。
    /// </summary>
    private void UpdateInstructionPreview()
    {
        var localLines = InstructionText.SplitLines(InstructionsText);
        var savedLines = _serverInstructionLines.Select(line => line.Text).ToArray();
        var hasUnsavedChanges = !string.Equals(SettingsFingerprint(), _loadedSettingsFingerprint, StringComparison.Ordinal);
        var differs = !localLines.SequenceEqual(savedLines, StringComparer.Ordinal);
        IReadOnlyList<string> shown;
        string source;
        if (savedLines.Length == 0)
        {
            shown = localLines;
            source = IsNewExam
                ? "新建考试：以下为当前填写内容的预览，保存后由服务器按计分规则生成学生端说明。"
                : "服务器没有返回已保存的说明，无法确认学生端当前显示；以下为当前填写内容的预览。";
        }
        else if (hasUnsavedChanges)
        {
            shown = localLines;
            source = differs
                ? "以下为尚未保存的更改预览，与服务器上的说明不同；保存后以服务器生成的说明为准。"
                : "以下为尚未保存的更改预览；本页生成结果与服务器上的说明一致。";
        }
        else
        {
            shown = savedLines;
            source = differs
                ? "以下为服务器保存、供学生端读取的说明；本页生成结果与它不同，请核对填写内容。"
                : "以下为服务器保存、供学生端读取的说明，与本页生成结果一致。";
        }
        InstructionPreview.Clear();
        foreach (var line in shown) InstructionPreview.Add(InstructionLineViewModel.From(line));
        InstructionSourceText = source;
    }

    /// <summary>设置指纹：只覆盖影响考生说明的字段（名称与科目题组），只改考试时间不算未保存的设置更改。</summary>
    private string SettingsFingerprint() =>
        JsonSerializer.Serialize(new { Name, Settings = ToSettings() });

    private string BuildInstructions()
    {
        var builder = new StringBuilder();
        foreach (var subject in Subjects)
        {
            builder.AppendLine($"{subject.Name.Trim()}（满分{subject.MaxScore.Trim()}分）");
            foreach (var group in subject.Groups.OrderBy(group => group.Order))
            {
                var pointsPerQuestion = group.Type switch
                {
                    "single" => group.Correct,
                    "multiple" => group.AllCorrect,
                    "indeterminate" when group.Correct == group.AllCorrect => group.Correct,
                    "indeterminate" => $"{group.Correct}分或{group.AllCorrect}",
                    _ => "题型无效"
                };
                var scoring = group.Type switch
                {
                    "single" => $"选对得{group.Correct}分，不选得{group.OneBlank}分，选错得{group.OneWrong}分。",
                    "multiple" => $"全部选对得{group.AllCorrect}分，选对但不全得{group.PartialCorrect}分，不选得{group.MultipleBlank}分，选错得{group.MultipleWrong}分。",
                    "indeterminate" => $"若有一个正确答案，选对得{group.Correct}分，不选得{group.OneBlank}分，选错得{group.OneWrong}分；若有多个正确答案，全部选对得{group.AllCorrect}分，选对但不全得{group.PartialCorrect}分，不选得{group.MultipleBlank}分，选错得{group.MultipleWrong}分。",
                    _ => "题型无效，无法生成计分说明。"
                };
                var abstain = group.AllowAbstain ? "本组允许选择“不作答”。" : string.Empty;
                // 与域层生成器同口径：名称与满分先 Trim，避免因服务端存了补齐空格的设置而误报“与服务器上的说明不同”。
                builder.AppendLine($"{group.Name.Trim()}：本题共{group.QuestionCount}小题，每小题{pointsPerQuestion}分，共计{group.MaxScore.Trim()}分。{scoring}{abstain}");
            }
        }
        return builder.ToString().TrimEnd();
    }

    private bool TryReadSchedule(out DateTimeOffset start, out DateTimeOffset end)
    {
        var startValid = DateTimeOffset.TryParse(
            ScheduledStartText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out start);
        var endValid = DateTimeOffset.TryParse(
            ScheduledEndText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out end);
        return startValid && endValid;
    }




    private void SetError(Exception exception) => Status = $"保存失败：{FriendlyErrors.Describe(exception)}";


}

public sealed class SubjectDraftViewModel : BindableBase
{
    private string _subjectId;
    private string _name;
    private string _maxScore;
    private GroupDraftViewModel? _selectedGroup;

    public SubjectDraftViewModel(string subjectId, string name, string maxScore)
    {
        _subjectId = subjectId;
        _name = name;
        _maxScore = maxScore;
    }

    public string SubjectId { get => _subjectId; set => SetProperty(ref _subjectId, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string MaxScore { get => _maxScore; set => SetProperty(ref _maxScore, value); }
    public ObservableCollection<GroupDraftViewModel> Groups { get; } = [];
    public GroupDraftViewModel? SelectedGroup { get => _selectedGroup; set => SetProperty(ref _selectedGroup, value); }
}

public sealed class GroupDraftViewModel : BindableBase
{
    private string _groupId = string.Empty;
    private string _name = "一、单项选择题";
    private bool _useDefaultName = true;
    private string _type = "single";
    private int _order = 1;
    private int _questionCount;
    private string _orderText = "1";
    private string _questionCountText = "0";
    private string _maxScore = "0";
    private bool _allowAbstain;
    private string _correct = "1";
    private string _oneBlank = "0";
    private string _oneWrong = "0";
    private string _allCorrect = "1";
    private string _partialCorrect = "0";
    private string _multipleBlank = "0";
    private string _multipleWrong = "0";

    public string GroupId { get => _groupId; set => SetProperty(ref _groupId, value); }
    public string Name
    {
        get => _name;
        set
        {
            _useDefaultName = value == DefaultName || value == $"{Order}、{TypeLabel}";
            SetProperty(ref _name, value);
        }
    }
    public string Type
    {
        get => _type;
        set
        {
            if (!SetProperty(ref _type, value)) return;
            OnPropertyChanged(nameof(TypeLabel));
            UpdateDefaultName();
        }
    }
    private string DefaultName => $"{FormatGroupNumber(Order)}、{TypeLabel}";
    private void UpdateDefaultName()
    {
        if (_useDefaultName && Order is >= 1 and <= 9999)
            SetProperty(ref _name, DefaultName, nameof(Name));
    }
    public string TypeLabel => Type switch { "single" => "单项选择题", "multiple" => "多项选择题", "indeterminate" => "不定项选择题", _ => "未选择" };
    // Preserve invalid input in the draft instead of letting WPF retain a previously valid integer.
    // The existing domain rules reject zero, so neither save path can submit that stale value.
    public int Order
    {
        get => _order;
        set => OrderText = value.ToString(CultureInfo.InvariantCulture);
    }
    public int QuestionCount
    {
        get => _questionCount;
        set { SetProperty(ref _questionCount, value); QuestionCountText = value.ToString(CultureInfo.InvariantCulture); }
    }
    public string OrderText
    {
        get => _orderText;
        set
        {
            if (SetProperty(ref _orderText, value))
            {
                SetProperty(ref _order, ParseDraftInteger(value), nameof(Order));
                UpdateDefaultName();
            }
        }
    }
    public string QuestionCountText
    {
        get => _questionCountText;
        set
        {
            if (SetProperty(ref _questionCountText, value))
                SetProperty(ref _questionCount, ParseDraftInteger(value), nameof(QuestionCount));
        }
    }
    private static string FormatGroupNumber(int number)
    {
        // Settings permit group orders from 1 to 9999.
        if (number is < 1 or > 9999) return number.ToString(CultureInfo.InvariantCulture);
        const string digits = "零一二三四五六七八九";
        string[] units = ["", "十", "百", "千"];
        var result = new System.Text.StringBuilder();
        var pendingZero = false;
        for (int divisor = 1000, unit = 3; divisor > 0; divisor /= 10, unit--)
        {
            var digit = number / divisor;
            number %= divisor;
            if (digit == 0)
            {
                if (result.Length > 0 && number > 0) pendingZero = true;
                continue;
            }
            if (pendingZero) result.Append('零');
            pendingZero = false;
            result.Append(digits[digit]).Append(units[unit]);
        }
        var text = result.ToString();
        return text.StartsWith("一十", StringComparison.Ordinal) ? text[1..] : text;
    }

    private static int ParseDraftInteger(string value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    public string MaxScore { get => _maxScore; set => SetProperty(ref _maxScore, value); }
    public bool AllowAbstain { get => _allowAbstain; set => SetProperty(ref _allowAbstain, value); }
    public string Correct { get => _correct; set => SetProperty(ref _correct, value); }
    public string OneBlank { get => _oneBlank; set => SetProperty(ref _oneBlank, value); }
    public string OneWrong { get => _oneWrong; set => SetProperty(ref _oneWrong, value); }
    public string AllCorrect { get => _allCorrect; set => SetProperty(ref _allCorrect, value); }
    public string PartialCorrect { get => _partialCorrect; set => SetProperty(ref _partialCorrect, value); }
    public string MultipleBlank { get => _multipleBlank; set => SetProperty(ref _multipleBlank, value); }
    public string MultipleWrong { get => _multipleWrong; set => SetProperty(ref _multipleWrong, value); }
}

public sealed record QuestionTypeOption(string Value, string Label);
