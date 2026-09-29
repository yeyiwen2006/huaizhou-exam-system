using Exam.Admin.Reporting;
using Exam.Domain.Reporting;

namespace Exam.AdminUnitTests.Reporting;

public sealed partial class ReportTableProjectionTests
{
    [Fact]
    public void Review_NoEligibleSampleIsExplicitInEveryMeanButRankStaysBlank()
    {
        var source = CreateResult();
        var subject = source.Subjects[0] with
        {
            SubjectMean = null,
            QuestionMeans = source.Subjects[0].QuestionMeans.Select(q => q with { Mean = null }).ToArray()
        };
        var tables = ReportTableProjection.Build(source with { Subjects = [subject], TotalMean = null });
        Assert.Equal("无有效样本", tables[0].MeanFooter![4]);
        Assert.Equal(string.Empty, tables[0].MeanFooter![5]);
        Assert.Equal("无有效样本", tables[0].MeanFooter![6]);
        Assert.Equal("无有效样本", tables[1].MeanFooter![4]);
        Assert.All(tables[3].MeanFooter!.Skip(4), cell => Assert.Equal("无有效样本", cell));
        Assert.Equal("无有效样本", tables[4].Rows[0].Cells[1].Text);
        Assert.Equal(string.Empty, tables[4].Rows[0].Cells[2].Text);
        Assert.Equal("无有效样本", tables[4].Rows[0].Cells[3].Text);
        Assert.Equal(string.Empty, tables[4].Rows[0].Cells[4].Text);
    }
}
