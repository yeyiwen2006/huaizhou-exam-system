using System.Globalization;
using System.Numerics;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.Domain.Reporting;

/// <summary>
/// Pure G01 calculation over one complete, version-consistent set of confirmed
/// submissions. This class neither decides report availability nor publishes it.
/// </summary>
public static class ReportCalculator
{
    public static ReportCalculationResult Calculate(ReportCalculationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (subjects, students) = ValidateAndSnapshot(input);
        var commonSample = students.Where(student =>
            subjects.All(subject => student.Grades[subject.SubjectId].FinalScore.Numerator.Sign > 0)).ToArray();

        var totalRanking = RankStudents(students, student => student.TotalScore);
        var subjectRankings = subjects.ToDictionary(subject => subject.SubjectId,
            subject => RankStudents(students, student => student.Grades[subject.SubjectId].FinalScore),
            StringComparer.Ordinal);
        var subjectRanksByCandidate = subjectRankings.ToDictionary(pair => pair.Key,
            pair => pair.Value.ToDictionary(row => row.Student.Participant.CandidateId, row => row.Rank,
                StringComparer.Ordinal), StringComparer.Ordinal);

        var totals = totalRanking.Select(row => new StudentTotalRow(
            row.Student.Participant, row.Student.TotalScore, row.Rank,
            Freeze(subjects.Select(subject => new StudentSubjectResult(subject.SubjectId,
                row.Student.Grades[subject.SubjectId].FinalScore,
                subjectRanksByCandidate[subject.SubjectId][row.Student.Participant.CandidateId]))))).ToArray();

        var subjectTables = subjects.Select(subject => BuildSubjectTables(subject,
            subjectRankings[subject.SubjectId], commonSample)).ToArray();
        var classes = BuildClassAverages(subjects, students, commonSample);

        return new(Freeze(totals), Freeze(subjectTables), classes,
            subjects.Aggregate(ExactRational.Zero, (sum, subject) => sum + subject.MaximumScore),
            Mean(commonSample.Select(student => student.TotalScore).ToArray()), commonSample.Length);
    }

    private static SubjectTables BuildSubjectTables(ReportSubject subject,
        IReadOnlyList<RankedStudent> ranking, IReadOnlyList<Student> commonSample)
    {
        var scores = ranking.Select(row => new SubjectScoreRow(row.Student.Participant,
            row.Student.Grades[subject.SubjectId].FinalScore, row.Rank));
        var answers = ranking.Select(row =>
        {
            var grade = row.Student.Grades[subject.SubjectId];
            var questions = grade.Questions.ToDictionary(question => question.QuestionId, StringComparer.Ordinal);
            return new SubjectAnswerRow(row.Student.Participant, row.Rank,
                Freeze(subject.Questions.Select(question => new AnswerCell(question.QuestionId,
                    questions[question.QuestionId].AnswerText,
                    questions[question.QuestionId].Outcome == AnswerOutcome.Correct))), grade.FinalScore);
        });
        var questionScores = ranking.Select(row =>
        {
            var grade = row.Student.Grades[subject.SubjectId];
            var questions = grade.Questions.ToDictionary(question => question.QuestionId, StringComparer.Ordinal);
            return new SubjectQuestionScoreRow(row.Student.Participant, row.Rank,
                Freeze(subject.Questions.Select(question => new QuestionScoreCell(question.QuestionId,
                    questions[question.QuestionId].Score,
                    Compare(questions[question.QuestionId].Score, question.MaximumScore) == 0))),
                grade.FinalScore);
        });
        var sampleQuestions = commonSample.Select(student => student.Grades[subject.SubjectId].Questions
            .ToDictionary(question => question.QuestionId, StringComparer.Ordinal)).ToArray();
        var questionMeans = subject.Questions.Select(question => new QuestionMean(question.QuestionId,
            Mean(sampleQuestions.Select(grades => grades[question.QuestionId].Score).ToArray())));

        return new(subject, Freeze(scores), Freeze(answers), Freeze(questionScores),
            Mean(commonSample.Select(student => student.Grades[subject.SubjectId].FinalScore).ToArray()),
            Freeze(questionMeans));
    }

    private static IReadOnlyList<ClassAverageRow> BuildClassAverages(IReadOnlyList<ReportSubject> subjects,
        IReadOnlyList<Student> students, IReadOnlyList<Student> commonSample)
    {
        var groups = students.GroupBy(student => student.Participant.ClassNumber, StringComparer.Ordinal)
            .OrderBy(group => BigInteger.Parse(group.Key, CultureInfo.InvariantCulture))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ClassMeanData(group.Key,
                commonSample.Where(student => student.Participant.ClassNumber == group.Key).ToArray(),
                subjects.ToDictionary(subject => subject.SubjectId,
                    subject => Mean(commonSample.Where(student => student.Participant.ClassNumber == group.Key)
                        .Select(student => student.Grades[subject.SubjectId].FinalScore).ToArray()),
                    StringComparer.Ordinal),
                Mean(commonSample.Where(student => student.Participant.ClassNumber == group.Key)
                    .Select(student => student.TotalScore).ToArray())))
            .ToArray();

        var totalRanks = RankClasses(groups, group => group.TotalMean);
        var subjectRanks = subjects.ToDictionary(subject => subject.SubjectId,
            subject => RankClasses(groups, group => group.SubjectMeans[subject.SubjectId]),
            StringComparer.Ordinal);
        return Freeze(groups.Select(group => new ClassAverageRow(group.ClassNumber, group.Sample.Length,
            Freeze(subjects.Select(subject => new ClassSubjectMean(subject.SubjectId,
                group.SubjectMeans[subject.SubjectId],
                FindRank(subjectRanks[subject.SubjectId], group.ClassNumber)))),
            group.TotalMean, FindRank(totalRanks, group.ClassNumber))));
    }

    private static Dictionary<string, int> RankClasses(IEnumerable<ClassMeanData> groups,
        Func<ClassMeanData, ReportMean?> selectMean)
    {
        var ordered = groups.Where(group => selectMean(group) is not null)
            .OrderByDescending(group => selectMean(group)!.Exact, RationalComparer.Instance)
            .ThenBy(group => BigInteger.Parse(group.ClassNumber, CultureInfo.InvariantCulture))
            .ThenBy(group => group.ClassNumber, StringComparer.Ordinal).ToArray();
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var rank = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i == 0 || Compare(selectMean(ordered[i])!.Exact, selectMean(ordered[i - 1])!.Exact) != 0)
                rank = i + 1;
            ranks.Add(ordered[i].ClassNumber, rank);
        }
        return ranks;
    }

    private static IReadOnlyList<RankedStudent> RankStudents(IEnumerable<Student> students,
        Func<Student, ExactRational> selectScore)
    {
        var ordered = students.OrderByDescending(selectScore, RationalComparer.Instance)
            .ThenBy(student => student.ExamNumberValue)
            .ThenBy(student => student.Participant.ExamNumber, StringComparer.Ordinal).ToArray();
        var rows = new RankedStudent[ordered.Length];
        var rank = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i == 0 || Compare(selectScore(ordered[i]), selectScore(ordered[i - 1])) != 0)
                rank = i + 1;
            rows[i] = new(ordered[i], rank);
        }
        return Array.AsReadOnly(rows);
    }

    private static (IReadOnlyList<ReportSubject> Subjects, IReadOnlyList<Student> Students)
        ValidateAndSnapshot(ReportCalculationInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ExamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.GradingVersion, 1);
        ArgumentNullException.ThrowIfNull(input.Subjects);
        ArgumentNullException.ThrowIfNull(input.Participants);
        ArgumentNullException.ThrowIfNull(input.Submissions);
        if (input.Subjects.Count == 0) throw new ArgumentException("报表缺少科目。");
        if (input.Participants.Count == 0)
            throw new ArgumentException("零实际参试者的报表语义尚未确认，不可自然开放空报表。");

        var subjectIds = new HashSet<string>(StringComparer.Ordinal);
        var subjectOrders = new HashSet<int>();
        var allQuestionIds = new HashSet<string>(StringComparer.Ordinal);
        var subjects = input.Subjects.Select(subject =>
        {
            ArgumentNullException.ThrowIfNull(subject);
            ArgumentException.ThrowIfNullOrWhiteSpace(subject.SubjectId);
            ArgumentException.ThrowIfNullOrWhiteSpace(subject.DisplayName);
            ArgumentNullException.ThrowIfNull(subject.MaximumScore);
            ArgumentNullException.ThrowIfNull(subject.Questions);
            if (subject.Order < 1 || !subjectOrders.Add(subject.Order) || !subjectIds.Add(subject.SubjectId) ||
                subject.MaximumScore.Numerator.Sign < 0 || subject.Questions.Count == 0)
                throw new ArgumentException("科目标识、顺序、满分或题目无效。");
            var questionIds = new HashSet<string>(StringComparer.Ordinal);
            var questionNos = new HashSet<int>();
            var questions = subject.Questions.Select(question =>
            {
                ArgumentNullException.ThrowIfNull(question);
                ArgumentException.ThrowIfNullOrWhiteSpace(question.QuestionId);
                ArgumentNullException.ThrowIfNull(question.MaximumScore);
                if (question.QuestionNo < 1 || !questionNos.Add(question.QuestionNo) ||
                    !questionIds.Add(question.QuestionId) || !allQuestionIds.Add(question.QuestionId) ||
                    question.MaximumScore.Numerator.Sign < 0)
                    throw new ArgumentException("题目标识、顺序或满分无效。");
                return question;
            }).OrderBy(question => question.QuestionNo).ToArray();
            return subject with { Questions = Array.AsReadOnly(questions) };
        }).OrderBy(subject => subject.Order).ToArray();

        var participants = new Dictionary<string, ReportParticipant>(StringComparer.Ordinal);
        var examNumbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in input.Participants)
        {
            ArgumentNullException.ThrowIfNull(participant);
            ArgumentException.ThrowIfNullOrWhiteSpace(participant.CandidateId);
            ArgumentException.ThrowIfNullOrWhiteSpace(participant.DisplayName);
            if (!IsDigits(participant.ExamNumber) || !IsDigits(participant.ClassNumber) ||
                !participants.TryAdd(participant.CandidateId, participant) ||
                !examNumbers.Add(participant.ExamNumber))
                throw new ArgumentException("参试者标识、编号或班级无效或重复。");
        }

        var submissionIds = new HashSet<string>(StringComparer.Ordinal);
        var students = new Dictionary<string, Student>(StringComparer.Ordinal);
        foreach (var submission in input.Submissions)
        {
            ArgumentNullException.ThrowIfNull(submission);
            ArgumentException.ThrowIfNullOrWhiteSpace(submission.SubmissionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(submission.CandidateId);
            ArgumentNullException.ThrowIfNull(submission.TotalScore);
            ArgumentNullException.ThrowIfNull(submission.Subjects);
            if (submission.ExamId != input.ExamId || submission.GradingVersion != input.GradingVersion ||
                !submissionIds.Add(submission.SubmissionId) ||
                !participants.TryGetValue(submission.CandidateId, out var participant) ||
                students.ContainsKey(submission.CandidateId) || submission.Subjects.Count != subjects.Length)
                throw new ArgumentException("答卷范围、考试或计分版本不一致。");

            var grades = new Dictionary<string, ReportSubjectGrade>(StringComparer.Ordinal);
            foreach (var grade in submission.Subjects)
            {
                ArgumentNullException.ThrowIfNull(grade);
                ArgumentException.ThrowIfNullOrWhiteSpace(grade.SubjectId);
                ArgumentNullException.ThrowIfNull(grade.FinalScore);
                ArgumentNullException.ThrowIfNull(grade.Questions);
                var definition = subjects.SingleOrDefault(subject => subject.SubjectId == grade.SubjectId);
                if (definition is null || grade.FinalScore.Numerator.Sign < 0 ||
                    grade.Questions.Count != definition.Questions.Count || grades.ContainsKey(grade.SubjectId))
                    throw new ArgumentException("单科成绩缺失、重复或含未知科目。");
                var questionIds = new HashSet<string>(StringComparer.Ordinal);
                var questionGrades = grade.Questions.Select(question =>
                {
                    ArgumentNullException.ThrowIfNull(question);
                    ArgumentException.ThrowIfNullOrWhiteSpace(question.QuestionId);
                    ArgumentNullException.ThrowIfNull(question.AnswerText);
                    ArgumentNullException.ThrowIfNull(question.Score);
                    if (!questionIds.Add(question.QuestionId) ||
                        !definition.Questions.Any(item => item.QuestionId == question.QuestionId) ||
                        !Enum.IsDefined(question.Outcome))
                        throw new ArgumentException("逐题成绩缺失、重复或含未知题目与判定。");
                    return question;
                }).ToArray();
                grades.Add(grade.SubjectId, grade with { Questions = Array.AsReadOnly(questionGrades) });
            }
            if (grades.Count != subjects.Length ||
                Compare(grades.Values.Aggregate(ExactRational.Zero, (sum, grade) => sum + grade.FinalScore),
                    submission.TotalScore) != 0)
                throw new ArgumentException("总分与单科最终成绩不一致。");
            students.Add(submission.CandidateId, new Student(participant!,
                BigInteger.Parse(participant!.ExamNumber, CultureInfo.InvariantCulture), grades,
                submission.TotalScore));
        }
        if (students.Count != participants.Count)
            throw new ArgumentException("实际参试者仍有答卷或成绩缺失。");
        return (Array.AsReadOnly(subjects), Array.AsReadOnly(students.Values.ToArray()));
    }

    private static bool IsDigits(string? text) => !string.IsNullOrEmpty(text) && text.All(char.IsAsciiDigit);

    private static int? FindRank(IReadOnlyDictionary<string, int> ranks, string classNumber) =>
        ranks.TryGetValue(classNumber, out var rank) ? rank : null;

    private static ReportMean? Mean(IReadOnlyList<ExactRational> values)
    {
        if (values.Count == 0) return null;
        var exact = values.Aggregate(ExactRational.Zero, (sum, value) => sum + value) /
            ExactRational.FromInteger(values.Count);
        return new(exact, values.Count, FormatTwoPlaces(exact));
    }

    // Provisional symmetric interpretation of 四舍五入. C05/C06 must confirm
    // negative midpoint handling before this formatter is used in production.
    private static string FormatTwoPlaces(ExactRational value)
    {
        var quotient = BigInteger.DivRem(BigInteger.Abs(value.Numerator) * 100,
            value.Denominator, out var remainder);
        if (remainder * 2 >= value.Denominator) quotient++;
        var whole = BigInteger.DivRem(quotient, 100, out var fractional);
        var sign = value.Numerator.Sign < 0 && quotient != 0 ? "-" : string.Empty;
        return sign + whole.ToString(CultureInfo.InvariantCulture) + "." +
            fractional.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
    }

    private static int Compare(ExactRational left, ExactRational right) =>
        (left.Numerator * right.Denominator).CompareTo(right.Numerator * left.Denominator);

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());

    private sealed record Student(ReportParticipant Participant, BigInteger ExamNumberValue,
        IReadOnlyDictionary<string, ReportSubjectGrade> Grades, ExactRational TotalScore);

    private sealed record RankedStudent(Student Student, int Rank);

    private sealed record ClassMeanData(string ClassNumber, Student[] Sample,
        IReadOnlyDictionary<string, ReportMean?> SubjectMeans, ReportMean? TotalMean);

    private sealed class RationalComparer : IComparer<ExactRational>
    {
        public static RationalComparer Instance { get; } = new();

        public int Compare(ExactRational? left, ExactRational? right) =>
            ReportCalculator.Compare(left!, right!);
    }
}
