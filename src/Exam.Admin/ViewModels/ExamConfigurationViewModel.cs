using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Versioning;

namespace Exam.Admin.ViewModels;

public sealed partial class ExamConfigurationViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _examId = "exam-draft";
    private string _name = "合成考试";
    private string _categoryName = "演示类别";
    private string _scheduledStartText = DateTimeOffset.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    private string _scheduledEndText = DateTimeOffset.Now.AddHours(3).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    private SubjectDraftViewModel? _selectedSubject;
    private string _status = "目前只有考试编号和起止时间会保存到服务器；名称、类别、科目、题组和计分规则只在本机编辑，关闭考务端后不保留。";
    private string _validationSummary = string.Empty;
    private string _instructionsText = string.Empty;
    private long? _expectedScheduleVersion;
    private long _contextVersion;
    private long _editVersion;
    private long _scheduleOperation;

    /// <summary>已读取或已保存的服务器考试时间版本；用于保存时的并发核对，不在界面显示。</summary>
    public long? LoadedScheduleVersion => _expectedScheduleVersion;

    public string ScheduleVersionText => _expectedScheduleVersion is { } version
        ? $"已读取服务器上的考试时间。保存时如果其他人已修改过，会提示重新读取，不会覆盖。"
        : "尚未读取服务器上的考试时间；保存时只会新建考试，不会覆盖已有考试。";

    private void ResetScheduleContext()
    {
        _contextVersion++;
        _expectedScheduleVersion = null;
        OnPropertyChanged(nameof(ScheduleVersionText));
    }

    public ExamConfigurationViewModel(AdminApiClient api)
    {
        _api = api;
        _api.ConfigurationChanged += (_, _) => ResetScheduleContext();
        AddSubjectCommand = new RelayCommand(AddSubject);
        RemoveSubjectCommand = new RelayCommand(RemoveSubject, () => SelectedSubject is not null);
        AddGroupCommand = new RelayCommand(AddGroup, () => SelectedSubject is not null);
        RemoveGroupCommand = new RelayCommand(RemoveGroup, () => SelectedSubject?.SelectedGroup is not null);
        RefreshInstructionsCommand = new RelayCommand(RefreshInstructions);
        SaveScheduleCommand = new AsyncRelayCommand(SaveScheduleAsync, onError: SetError);
        LoadScheduleCommand = new AsyncRelayCommand(LoadScheduleAsync, onError: SetError);
        AddSubject();
        RefreshInstructions();
    }

    public ObservableCollection<SubjectDraftViewModel> Subjects { get; } = [];
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
    public string CategoryName { get => _categoryName; set => SetProperty(ref _categoryName, value); }
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
        Status = $"已读取服务器上的考试时间。名称、科目、题组和计分规则仍只在本机，尚未保存到服务器。";
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ExamId) || ExamId.Trim().Length > 128)
        {
            errors.Add("考试编号必须为 1–128 个字符。");
        }

        if (!IsValidWindowsFileName(Name))
        {
            errors.Add("考试名称不能为空，且不能包含 \\ / : * ? \" < > | 等字符。");
        }

        if (!TryReadSchedule(out var start, out var end))
        {
            errors.Add("请按“年-月-日 时:分”填写开始和结束时间，且结束时间要晚于开始时间。");
        }
        else if (end <= start)
        {
            errors.Add("结束时间必须晚于开始时间。");
        }

        if (Subjects.Count == 0)
        {
            errors.Add("至少配置一个科目。");
        }

        var subjectIds = new HashSet<string>(StringComparer.Ordinal);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in Subjects)
        {
            if (string.IsNullOrWhiteSpace(subject.SubjectId) || subject.SubjectId.Trim().Length > 128 ||
                !subjectIds.Add(subject.SubjectId.Trim()))
            {
                errors.Add($"科目“{subject.Name}”的编号为空或与其他科目重复。");
            }
            if (string.IsNullOrWhiteSpace(subject.Name) || subject.Name.Length > 64)
            {
                errors.Add($"有科目没有填写名称。");
            }
            if (subject.MaxScore.Trim().Length > 32 || !ScoreConstant().IsMatch(subject.MaxScore.Trim()))
            {
                errors.Add($"科目“{subject.Name}”的满分必须是数字，例如 100 或 7.5。");
            }
            if (subject.Groups.Count == 0)
            {
                errors.Add($"科目“{subject.Name}”至少需要一个题组。");
            }

            var orders = new HashSet<int>();
            foreach (var group in subject.Groups)
            {
                ValidateGroup(subject, group, groupIds, orders, errors);
            }
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
        var subject = new SubjectDraftViewModel($"subject-{number}", $"科目{number}", "10");
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
            Name = $"{number}、单项选择题",
            Type = "single",
            Order = number,
            QuestionCount = 10,
            MaxScore = "10"
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
        // A05 creates version 1 and increments exactly once for each conditional update.
        // Capture before awaiting: a positive but stale/unrelated version is not a save receipt.
        var savedVersion = expectedVersion is { } version ? checked(version + 1) : 1;
        var schedule = new ScheduleWindow(start, end);
        var snapshot = await _api.SaveScheduleAsync(examId, schedule, expectedVersion);
        if (context != _contextVersion || operation != _scheduleOperation) return;
        if (!StringComparer.Ordinal.Equals(snapshot.ExamId, examId) ||
            snapshot.Schedule != schedule || snapshot.Versions is null ||
            snapshot.Versions.ScheduleVersion != savedVersion)
            throw new InvalidOperationException("无法确认保存结果，请点击“读取服务器上的时间”核对后再操作。");
        _expectedScheduleVersion = snapshot.Versions.ScheduleVersion;
        OnPropertyChanged(nameof(ScheduleVersionText));
        Status = edits == _editVersion
            ? $"考试时间已保存到服务器。名称、类别、科目、题组和计分规则仍只在本机，尚未保存到服务器。"
            : $"此前填写的考试时间已保存；保存期间修改的时间尚未保存，请核对后再次保存。";
    }

    private void RefreshInstructions()
    {
        var errors = Validate();
        ValidationSummary = errors.Count == 0
            ? "填写格式检查通过。计分规则能否算出正确的满分，要在导入试题和答案时再核对。"
            : string.Join(Environment.NewLine, errors.Select(error => $"• {error}"));
        InstructionsText = BuildInstructions();
    }

    private string BuildInstructions()
    {
        var builder = new StringBuilder();
        foreach (var subject in Subjects)
        {
            builder.AppendLine($"{subject.Name}（满分{subject.MaxScore}分）");
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
                builder.AppendLine($"{group.Name}：本题共{group.QuestionCount}小题，每小题{pointsPerQuestion}分，共计{group.MaxScore}分。{scoring}{abstain}");
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

    private static void ValidateGroup(
        SubjectDraftViewModel subject,
        GroupDraftViewModel group,
        ISet<string> groupIds,
        ISet<int> orders,
        ICollection<string> errors)
    {
        var label = $"科目“{subject.Name}”题组“{group.Name}”";
        if (string.IsNullOrWhiteSpace(group.GroupId) || group.GroupId.Trim().Length > 128 ||
            !groupIds.Add(group.GroupId.Trim()))
        {
            errors.Add($"{label}的编号为空或与其他题组重复。");
        }
        if (string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 64)
        {
            errors.Add($"{label}缺少名称。");
        }
        if (group.Type is not ("single" or "multiple" or "indeterminate"))
        {
            errors.Add($"{label}的题型只能是单项选择题、多项选择题或不定项选择题。");
        }
        if (group.Order is < 1 or > 9999 || !orders.Add(group.Order))
        {
            errors.Add($"{label}的顺序必须是 1–9999 的整数，且同一科目内不能重复。");
        }
        if (group.QuestionCount is < 1 or > 9999)
        {
            errors.Add($"{label}的题量必须是 1–9999 的整数。");
        }
        if (group.MaxScore.Trim().Length > 32 || !ScoreConstant().IsMatch(group.MaxScore.Trim()))
        {
            errors.Add($"{label}的满分必须是数字，例如 30 或 7.5。");
        }

        if (group.Type is "single" or "indeterminate")
        {
            ValidateExpressions(label, errors, group.Correct, group.OneBlank, group.OneWrong);
        }
        if (group.Type is "multiple" or "indeterminate")
        {
            ValidateExpressions(label, errors, group.AllCorrect, group.PartialCorrect, group.MultipleBlank, group.MultipleWrong);
        }
    }

    private static void ValidateExpressions(string label, ICollection<string> errors, params string[] expressions)
    {
        if (expressions.Any(expression => string.IsNullOrWhiteSpace(expression) || expression.Length > 200 ||
                                          !ScoreExpression().IsMatch(expression)))
        {
            errors.Add($"{label}的计分规则只能使用数字、加减乘除、括号和 {{正确选项总数}}、{{选择的正确选项数}} 两个变量。");
        }
    }

    private static bool IsValidWindowsFileName(string name)
    {
        var value = name;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.EndsWith(' ') || value.EndsWith('.'))
        {
            return false;
        }
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        var stem = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
               !Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", RegexOptions.CultureInvariant);
    }

    private void SetError(Exception exception) => Status = $"保存失败：{FriendlyErrors.Describe(exception)}";

    [GeneratedRegex("^[+-]?[0-9]+(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ScoreConstant();

    [GeneratedRegex("^(?![\\s]*$)(?:[0-9.+\\-*/() \\t]|\\{正确选项总数\\}|\\{选择的正确选项数\\})+$", RegexOptions.CultureInvariant)]
    private static partial Regex ScoreExpression();
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
    private string _name = string.Empty;
    private string _type = "single";
    private int _order = 1;
    private int _questionCount = 1;
    private string _maxScore = "1";
    private bool _allowAbstain;
    private string _correct = "1";
    private string _oneBlank = "0";
    private string _oneWrong = "0";
    private string _allCorrect = "1";
    private string _partialCorrect = "0";
    private string _multipleBlank = "0";
    private string _multipleWrong = "0";

    public string GroupId { get => _groupId; set => SetProperty(ref _groupId, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Type { get => _type; set => SetProperty(ref _type, value); }
    public int Order { get => _order; set => SetProperty(ref _order, value); }
    public int QuestionCount { get => _questionCount; set => SetProperty(ref _questionCount, value); }
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
