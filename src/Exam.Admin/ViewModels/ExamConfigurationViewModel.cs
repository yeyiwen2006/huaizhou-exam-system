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
    private string _status = "可离线编辑完整结构；服务器当前只能持久化 examId 和起止时间。";
    private string _validationSummary = string.Empty;
    private string _instructionsText = string.Empty;

    public ExamConfigurationViewModel(AdminApiClient api)
    {
        _api = api;
        AddSubjectCommand = new RelayCommand(AddSubject);
        RemoveSubjectCommand = new RelayCommand(RemoveSubject, () => SelectedSubject is not null);
        AddGroupCommand = new RelayCommand(AddGroup, () => SelectedSubject is not null);
        RemoveGroupCommand = new RelayCommand(RemoveGroup, () => SelectedSubject?.SelectedGroup is not null);
        RefreshInstructionsCommand = new RelayCommand(RefreshInstructions);
        SaveScheduleCommand = new AsyncRelayCommand(SaveScheduleAsync, onError: SetError);
        AddSubject();
        RefreshInstructions();
    }

    public ObservableCollection<SubjectDraftViewModel> Subjects { get; } = [];
    public IReadOnlyList<string> QuestionTypes { get; } = ["single", "multiple", "indeterminate"];

    public string ExamId { get => _examId; set => SetProperty(ref _examId, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string CategoryName { get => _categoryName; set => SetProperty(ref _categoryName, value); }
    public string ScheduledStartText { get => _scheduledStartText; set => SetProperty(ref _scheduledStartText, value); }
    public string ScheduledEndText { get => _scheduledEndText; set => SetProperty(ref _scheduledEndText, value); }

    public SubjectDraftViewModel? SelectedSubject
    {
        get => _selectedSubject;
        set
        {
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

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ExamId) || ExamId.Trim().Length > 128)
        {
            errors.Add("考试 ID 必须为 1–128 个字符。");
        }

        if (!IsValidWindowsFileName(Name))
        {
            errors.Add("考试名称不能为空，且必须可用作 Windows 文件名。");
        }

        if (!TryReadSchedule(out var start, out var end))
        {
            errors.Add("开始和结束时间必须采用有效日期时间，且结束时间晚于开始时间。");
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
        foreach (var subject in Subjects)
        {
            if (string.IsNullOrWhiteSpace(subject.SubjectId) || !subjectIds.Add(subject.SubjectId.Trim()))
            {
                errors.Add($"科目“{subject.Name}”的标识为空或重复。");
            }
            if (string.IsNullOrWhiteSpace(subject.Name))
            {
                errors.Add($"科目 {subject.SubjectId} 缺少名称。");
            }
            if (!ScoreConstant().IsMatch(subject.MaxScore.Trim()))
            {
                errors.Add($"科目“{subject.Name}”满分必须是有限十进制常量。");
            }
            if (subject.Groups.Count == 0)
            {
                errors.Add($"科目“{subject.Name}”至少需要一个题组。");
            }

            var groupIds = new HashSet<string>(StringComparer.Ordinal);
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
        var number = Subjects.Count + 1;
        var subject = new SubjectDraftViewModel($"subject-{number}", $"科目{number}", "100");
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
        var number = SelectedSubject.Groups.Count + 1;
        var group = new GroupDraftViewModel
        {
            GroupId = $"{SelectedSubject.SubjectId}-group-{number}",
            Name = $"{number}、单项选择题",
            Type = "single",
            Order = number,
            QuestionCount = 10,
            MaxScore = "30"
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

    private async Task SaveScheduleAsync()
    {
        RefreshInstructions();
        var errors = Validate();
        if (errors.Count > 0)
        {
            Status = "配置校验失败，未发送服务器请求。";
            return;
        }

        TryReadSchedule(out var start, out var end);
        Status = "正在保存服务器当前支持的考试计划字段…";
        var snapshot = await _api.SaveScheduleAsync(ExamId.Trim(), new ScheduleWindow(start, end));
        Status = $"计划已保存：scheduleVersion={snapshot.Versions.ScheduleVersion}。名称、类别、科目、题组和计分规则仍为本地草稿，等待 A 的正式元数据 API。";
    }

    private void RefreshInstructions()
    {
        var errors = Validate();
        ValidationSummary = errors.Count == 0
            ? "本地字段已按审阅到的 D01 分支 schema 结构校验；正式 schema 尚未合入当前主线，表达式求值和满分一致性仍须 D04 服务验证。"
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
                var scoring = group.Type switch
                {
                    "single" => $"选对得{group.Correct}分，不选得{group.OneBlank}分，选错得{group.OneWrong}分。",
                    "multiple" => $"全部选对得{group.AllCorrect}分，选对但不全得{group.PartialCorrect}分，不选得{group.MultipleBlank}分，选错得{group.MultipleWrong}分。",
                    "indeterminate" => $"若有一个正确答案，选对得{group.Correct}分，不选得{group.OneBlank}分，选错得{group.OneWrong}分；若有多个正确答案，全部选对得{group.AllCorrect}分，选对但不全得{group.PartialCorrect}分，不选得{group.MultipleBlank}分，选错得{group.MultipleWrong}分。",
                    _ => "题型无效，无法生成计分说明。"
                };
                builder.AppendLine($"{group.Name}：本题共{group.QuestionCount}小题，共计{group.MaxScore}分。{scoring}");
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
        if (string.IsNullOrWhiteSpace(group.GroupId) || !groupIds.Add(group.GroupId.Trim()))
        {
            errors.Add($"{label}的标识为空或重复。");
        }
        if (string.IsNullOrWhiteSpace(group.Name))
        {
            errors.Add($"{label}缺少名称。");
        }
        if (group.Type is not ("single" or "multiple" or "indeterminate"))
        {
            errors.Add($"{label}题型只能是 single、multiple 或 indeterminate。");
        }
        if (group.Order is < 1 or > 9999 || !orders.Add(group.Order))
        {
            errors.Add($"{label}顺序必须为 1–9999 且在科目内唯一。");
        }
        if (group.QuestionCount is < 1 or > 9999)
        {
            errors.Add($"{label}题量必须为 1–9999。");
        }
        if (!ScoreConstant().IsMatch(group.MaxScore.Trim()))
        {
            errors.Add($"{label}满分必须是有限十进制常量。");
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
        if (expressions.Any(expression => string.IsNullOrWhiteSpace(expression) || !ScoreExpression().IsMatch(expression)))
        {
            errors.Add($"{label}计分表达式只能使用有限十进制、两个规定变量、四则运算和括号。");
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
        var stem = Path.GetFileNameWithoutExtension(value).ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
               !Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", RegexOptions.CultureInvariant);
    }

    private void SetError(Exception exception) => Status = $"保存失败：{exception.Message}";

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
