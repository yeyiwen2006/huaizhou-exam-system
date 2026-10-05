using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Domain.Exams;

namespace Exam.AdminUnitTests;

/// <summary>
/// C01 第 2 项：考生说明预览的字体分段与来源。
/// 预览只渲染结构化配置生成的文字，字母表式的“从预览文字反推配置”不被支持；
/// 未保存更改时显示当前填写内容的预览，否则回显服务器保存的（学生端显示的）说明。
/// </summary>
public sealed class InstructionPreviewTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public void Segment_AssignsNumbersToTimesNewRomanAndBodyToSimHei()
    {
        var segments = InstructionText.Segment("选对得3分，选错得-0.75分。");

        Assert.Equal(new[]
        {
            ("选对得", InstructionText.BodyFont),
            ("3", InstructionText.NumberFont),
            ("分，选错得-", InstructionText.BodyFont),
            ("0.75", InstructionText.NumberFont),
            ("分。", InstructionText.BodyFont),
        }, segments.Select(item => (item.Text, item.FontFamily)));
    }

    [Fact]
    public void Segment_EmptyLineStillProducesOneBodySegment()
    {
        var segment = Assert.Single(InstructionText.Segment(string.Empty));

        Assert.Equal(string.Empty, segment.Text);
        Assert.Equal(InstructionText.BodyFont, segment.FontFamily);
    }

    [Fact]
    public void SplitLines_DropsBlankLinesAndNormalizesLineEndings()
    {
        var lines = InstructionText.SplitLines("甲（满分10分）\r\n\r\n乙（满分20分）\n   \n丙（满分30分）");

        Assert.Equal(new[] { "甲（满分10分）", "乙（满分20分）", "丙（满分30分）" }, lines);
    }

    [Fact]
    public async Task Load_EchoesServerInstructionsAndSaysTheyAreWhatStudentsSee()
    {
        var settings = Settings("3");
        var api = Configured(new Handler(_ => Success(Detail(settings))));
        var viewModel = new ExamConfigurationViewModel(api);

        await viewModel.OpenAsync("exam-1");

        // 服务器保存的说明按域层生成器产出，本页预览与它逐行一致，说明字体分段不改写内容。
        var saved = ExamSettingsRules.BuildInstructions(settings);
        Assert.Equal(saved, viewModel.InstructionPreview.Select(line => line.Text));
        Assert.Equal(saved, InstructionText.SplitLines(viewModel.InstructionsText));
        Assert.Contains("与学生端显示一致", viewModel.InstructionSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_ShowsPendingPreviewAndFlagsItDiffersFromTheServerCopy()
    {
        var settings = Settings("3");
        var api = Configured(new Handler(_ => Success(Detail(settings))));
        var viewModel = new ExamConfigurationViewModel(api);
        await viewModel.OpenAsync("exam-1");

        // 改一个分值但先不保存：预览必须变成当前填写内容，并说明与服务器上的说明不同。
        viewModel.Subjects[0].Groups[0].Correct = "5";
        viewModel.RefreshInstructionsCommand.Execute(null);

        Assert.Contains("尚未保存的更改预览", viewModel.InstructionSourceText, StringComparison.Ordinal);
        Assert.Contains("与服务器上的说明不同", viewModel.InstructionSourceText, StringComparison.Ordinal);
        Assert.Contains(viewModel.InstructionPreview, line => line.Text.Contains("选对得5分", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.InstructionPreview, line => line.Text.Contains("选对得3分", StringComparison.Ordinal));
    }

    [Fact]
    public void NewExam_SaysTheServerGeneratesInstructionsOnSave()
    {
        // 新建考试没有服务器说明，预览必须说明“保存后由服务器生成”，不能冒充学生端当前显示的内容。
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        viewModel.ResetForNew();

        Assert.Contains("新建考试", viewModel.InstructionSourceText, StringComparison.Ordinal);
        Assert.NotEmpty(viewModel.InstructionPreview);
        Assert.All(viewModel.InstructionPreview, line => Assert.NotEmpty(line.Segments));
    }

    [Fact]
    public async Task ConnectionChange_ClearsTheServerCopyAndThePreview()
    {
        var api = Configured(new Handler(_ => Success(Detail(Settings("3")))));
        var viewModel = new ExamConfigurationViewModel(api);
        await viewModel.OpenAsync("exam-1");
        Assert.NotEmpty(viewModel.InstructionPreview);

        api.Configure("https://localhost:5002/", "synthetic-token", "operator-1");

        Assert.Empty(viewModel.InstructionPreview);
        Assert.Empty(viewModel.InstructionSourceText);
    }

    [Fact]
    public async Task Load_WithoutServerInstructionsKeepsThePageUsableAndSaysSo()
    {
        // 服务器还没有生成说明（例如学生端说明尚未就绪）不是读取失败：页面必须仍可改设置、时间与可见性，
        // 并如实说明“无法确认学生端当前显示”，改为预览本地填写内容。
        var settings = Settings("3");
        var api = Configured(new Handler(_ => Success(Detail(settings) with
        {
            InstructionLines = Array.Empty<string>(),
        })));
        var viewModel = new ExamConfigurationViewModel(api);

        await viewModel.OpenAsync("exam-1");

        Assert.Contains("服务器没有返回已保存的说明", viewModel.InstructionSourceText, StringComparison.Ordinal);
        Assert.Equal(InstructionText.SplitLines(viewModel.InstructionsText),
            viewModel.InstructionPreview.Select(line => line.Text));
        Assert.True(viewModel.CanSave);
        Assert.True(viewModel.CanEditTime);
    }

    [Fact]
    public async Task Load_PaddedStoredSettingsDoNotWarnAboutDifferences()
    {
        // 服务端可能存着补齐空格的设置，而说明由域层按 Trim 后的值生成：本地预览必须同口径，
        // 否则一打开考试就会误报“本页生成结果与它不同，请核对填写内容”。
        var padded = new ExamSettings(
        [
            new ExamSubjectSettings("math", " 合成数学 ", " 6 ",
            [
                new ExamGroupSettings("math-1", "一、单项选择题 ", "single", 1, 2, " 6 ", false,
                    new ManifestOneCorrect(" 3 ", "0", "-0.75"), null),
            ]),
        ]);
        var api = Configured(new Handler(_ => Success(new ExamDetail(Summary(), padded, 1,
            ExamSettingsRules.BuildInstructions(padded)))));
        var viewModel = new ExamConfigurationViewModel(api);

        await viewModel.OpenAsync("exam-1");

        Assert.Contains("与学生端显示一致", viewModel.InstructionSourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("不同", viewModel.InstructionSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateReceiptWithoutStructuredSettingsIsRejected()
    {
        // 新建/保存的回执也校验结构：缺字段必须在页面接上之前失败，
        // 否则会先抛空引用、把一次已经提交的写入报成“保存未完成”。
        var api = Configured(new Handler(_ => Success(Detail(Settings("3")) with { Settings = null! })));

        var error = await Assert.ThrowsAsync<AdminApiException>(() =>
            api.CreateExamAsync("合成新考试", new Exam.Contracts.Versioning.ScheduleWindow(
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1)), Settings("3")));

        Assert.Equal("INVALID_RESPONSE", error.Code);
    }

    private static ExamSummary Summary() =>
        new("exam-1", "合成模拟考试", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2),
            ExamLifecycle.Scheduled, false, false, 0, 0, 1);

    private static ExamSettings Settings(string correct) => new(
    [
        new ExamSubjectSettings("math", "合成数学", "6",
        [
            new ExamGroupSettings("math-1", "一、单项选择题", "single", 1, 2, "6", false,
                new ManifestOneCorrect(correct, "0", "-0.75"), null),
        ]),
    ]);

    private static ExamDetail Detail(ExamSettings settings) =>
        new(Summary(), settings, 1, ExamSettingsRules.BuildInstructions(settings));

    private static AdminApiClient Configured(HttpMessageHandler handler)
    {
        var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        return api;
    }

    private static HttpResponseMessage Success<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "test", value, null),
            options: Json),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
