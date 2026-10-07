using Exam.Admin.Services;
using Exam.Admin.ViewModels;

namespace Exam.AdminUnitTests;

public sealed class GroupDraftIntegerTests
{
    [Fact]
    public void DefaultNameFollowsTypeAndOrderButPreservesACustomName()
    {
        var group = new GroupDraftViewModel();
        Assert.Equal("一、单项选择题", group.Name);
        group.Type = "indeterminate";
        Assert.Equal("一、不定项选择题", group.Name);
        group.Type = "multiple";
        Assert.Equal("一、多项选择题", group.Name);
        group.OrderText = "12";
        Assert.Equal("十二、多项选择题", group.Name);
        group.OrderText = "";
        group.OrderText = "101";
        Assert.Equal("一百零一、多项选择题", group.Name);
        group.Name = "自定义综合题组";
        group.Type = "single"; group.Order = 2;
        Assert.Equal("自定义综合题组", group.Name);
    }

    [Fact]
    public void LoadedDefaultNamesContinueFollowingChanges()
    {
        var group = new GroupDraftViewModel { Type = "multiple", Order = 2, Name = "二、多项选择题" };
        group.Type = "single";
        Assert.Equal("二、单项选择题", group.Name);
        group.Name = "2、单项选择题";
        group.Type = "indeterminate";
        Assert.Equal("二、不定项选择题", group.Name);
    }
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public void InvalidNumericDraftCannotRetainThePreviouslyValidValue(string invalid)
    {
        using var api = new AdminApiClient();
        var exam = new ExamConfigurationViewModel(api);
        var group = exam.Subjects[0].Groups[0];
        group.QuestionCount = 10;
        group.Order = 1;
        group.QuestionCountText = invalid;
        group.OrderText = invalid;
        Assert.Equal(invalid, group.QuestionCountText);
        Assert.Equal(invalid, group.OrderText);
        Assert.Equal(0, group.QuestionCount);
        Assert.Equal(0, group.Order);
        Assert.Contains(exam.Validate(), error => error.Contains("题量", StringComparison.Ordinal));
        Assert.Contains(exam.Validate(), error => error.Contains("顺序", StringComparison.Ordinal));
        group.QuestionCountText = "10";
        group.OrderText = "1";
        Assert.Equal(10, group.QuestionCount);
        Assert.Equal(1, group.Order);
        Assert.Empty(exam.Validate());
    }

    [Fact]
    public void LoadingIntegersAndChangingQuestionTypeKeepTheDraftConsistent()
    {
        var group = new GroupDraftViewModel { QuestionCount = 12, Order = 2, AllCorrect = "3" };
        Assert.Equal("12", group.QuestionCountText); Assert.Equal("2", group.OrderText);
        group.QuestionCountText = "not-an-integer";
        group.Type = "multiple"; group.Type = "single"; group.Type = "indeterminate";
        Assert.Equal("not-an-integer", group.QuestionCountText);
        Assert.Equal(0, group.QuestionCount); Assert.Equal("3", group.AllCorrect);
        group.QuestionCount = 12;
        Assert.Equal("12", group.QuestionCountText);
        Assert.Equal(12, group.QuestionCount);
    }
}
