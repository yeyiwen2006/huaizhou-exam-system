using Exam.Contracts.Grading;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Reporting;

public sealed partial class ReportCalculatorTests
{
    [Fact]
    public void Review_RejectsSubjectMaximumNotEqualToQuestionMaximumSum()
    {
        var input = CreateInput([("review", "1", "01", R(2), R(3))]);
        input = input with { Subjects = [input.Subjects[0] with { MaximumScore = R(31) }, input.Subjects[1]] };
        Assert.Throws<ArgumentException>(() => ReportCalculator.Calculate(input));
    }

    [Fact]
    public void Review_RejectsSubjectFinalScoreNotEqualToFlooredQuestionSum()
    {
        var input = CreateInput([("review", "1", "01", R(2), R(3))]);
        var original = input.Submissions[0];
        var grade = original.Subjects[0] with
        {
            Questions = [original.Subjects[0].Questions[0] with { Score = R(-1) }]
        };
        input = input with { Submissions = [original with { Subjects = [grade, original.Subjects[1]] }] };
        Assert.Throws<ArgumentException>(() => ReportCalculator.Calculate(input));
    }
    [Theory]
    [InlineData(-3, 2, 0)]
    [InlineData(-3, 3, 0)]
    [InlineData(-1, 3, 2)]
    public void Review_NegativeQuestionScoresAreSummedBeforeSubjectFlooring(int first, int second, int final)
    {
        var input = CreateInput([("review", "1", "01", R(final), R(3))]);
        var original = input.Submissions[0];
        var subject = original.Subjects[0] with
        {
            Questions = [new("q-a", "A", AnswerOutcome.Wrong, R(first)),
                new("q-a-positive", "B", AnswerOutcome.PartialCorrect, R(second))]
        };
        input = input with
        {
            Subjects = [input.Subjects[0] with
                { Questions = [new("q-a", 1, R(15)), new("q-a-positive", 2, R(15))] }, input.Subjects[1]],
            Submissions = [original with { Subjects = [subject, original.Subjects[1]] }]
        };
        var result = ReportCalculator.Calculate(input);
        Assert.Equal(final > 0 ? 1 : 0, result.CommonSampleCount);
        if (final > 0) Assert.Equal(R(final), result.Subjects[0].SubjectMean!.Exact);
        else Assert.Null(result.Subjects[0].SubjectMean);
        Assert.Single(result.StudentTotals);
    }

    [Theory]
    [InlineData(2005, 1000, "2.01")]
    [InlineData(20049, 10000, "2.00")]
    public void Review_MeanDisplayRoundsExactValuesOnlyAtTheFinalBoundary(int numerator, int denominator, string display)
    {
        var score = ExactRational.Create(numerator, denominator);
        var input = CreateInput([("review", "1", "01", score, R(1))]);
        var result = ReportCalculator.Calculate(input);
        Assert.Equal(score, result.Subjects[0].SubjectMean!.Exact);
        Assert.Equal(display, result.Subjects[0].SubjectMean!.Display);
    }

    [Theory]
    [InlineData(-201,200,"-1.01")]
    [InlineData(-1,200,"-0.01")]
    [InlineData(-1,1000,"0.00")]
    public void Review_NegativeQuestionMeanRoundsAwayFromZeroWithoutNegativeZero(int numerator,int denominator,string display)
    {
        var input=CreateInput([("synthetic-review","1","01",R(1),R(1))]);
        var negative=ExactRational.Create(numerator,denominator); var final=negative+R(2);
        var submission=input.Submissions[0]; var subject=submission.Subjects[0];
        input=input with
        {
            Subjects=[input.Subjects[0] with { Questions=[new("q-a",1,R(15)),new("q-a-positive",2,R(15))] },input.Subjects[1]],
            Submissions=[submission with { TotalScore=final+R(1),Subjects=[subject with
            {
                FinalScore=final,Questions=[new("q-a","B",AnswerOutcome.Wrong,negative),
                    new("q-a-positive","A",AnswerOutcome.PartialCorrect,R(2))]
            },submission.Subjects[1]] }]
        };
        var report=ReportCalculator.Calculate(input);
        Assert.Equal(negative,report.Subjects[0].QuestionMeans[0].Mean!.Exact);
        Assert.Equal(display,report.Subjects[0].QuestionMeans[0].Mean!.Display);
        ReportResultShapeValidator.Validate(report);
    }

}
