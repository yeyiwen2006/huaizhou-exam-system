using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;

namespace Exam.Contracts.Exams;

/// <summary>
/// 考务端管理的考试目录。考试名称是考务人员区分考试的唯一业务依据；
/// examId 由服务器生成，只用于内部关联，界面不显示。
/// </summary>
public sealed record ExamSettings(
    IReadOnlyList<ExamSubjectSettings> Subjects);

/// <summary>科目设置；分组规则字段与导入清单（清单校验 scoring-rules）一致。</summary>
public sealed record ExamSubjectSettings(
    string SubjectId,
    string Name,
    string MaxScore,
    IReadOnlyList<ExamGroupSettings> Groups);

public sealed record ExamGroupSettings(
    string GroupId,
    string Name,
    string Type,
    int Order,
    int QuestionCount,
    string MaxScore,
    bool AllowAbstain,
    ManifestOneCorrect? OneCorrect,
    ManifestMultipleCorrect? MultipleCorrect);

public sealed record ExamSummary(
    string ExamId,
    string Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    ExamLifecycle Lifecycle,
    bool VisibleToStudents,
    bool VisibilityLocked,
    int ActualParticipants,
    int CollectedSubmissions,
    long SettingsVersion)
{
    /// <summary>服务器当前试卷版本；0 表示未发布，null 表示未提供发布状态。</summary>
    public long? PaperVersion { get; init; }
}

public sealed record ExamDetail(
    ExamSummary Summary,
    ExamSettings Settings,
    long ScheduleVersion,
    IReadOnlyList<string> InstructionLines);

public sealed record CreateExamRequest(
    string RequestId,
    string Name,
    ScheduleWindow Schedule,
    ExamSettings Settings);

/// <summary>修改名称与结构；只允许在考试开始前进行。起止时间仍经 时间修改接口修改。</summary>
public sealed record UpdateExamSettingsRequest(
    string RequestId,
    long ExpectedVersion,
    string Name,
    ExamSettings Settings);

public sealed record SetExamVisibilityRequest(
    string RequestId,
    bool VisibleToStudents);

public static class ExamCatalogErrorCodes
{
    public const string NameTaken = "EXAM_NAME_TAKEN";
    public const string VisibilityLocked = "EXAM_VISIBILITY_LOCKED";
    public const string SettingsLocked = "EXAM_SETTINGS_LOCKED";
}
