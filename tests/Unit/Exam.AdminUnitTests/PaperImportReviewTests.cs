using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;

namespace Exam.AdminUnitTests;

/// <summary>
/// 试题导入页的**对抗性**用例：预检结论会不会被挪用到另一份输入上、失败会不会留下假通过、
/// 非法输入会不会被静默换成上一次的合法值。全部使用合成材料路径，不接触真实试卷材料，不需要 TeX。
/// </summary>
public sealed partial class PaperImportViewModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1 000")]
    [InlineData("99999999999999999999")]
    [InlineData("第二版")]
    public async Task Review_UnusableTargetVersionIsRejectedInsteadOfFallingBackToTheLastAcceptedValue(string text)
    {
        var paths = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Success(PreviewPassed));
        });
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成预检";
        vm.TargetVersionText = "2";
        await vm.PreviewAsync();
        Assert.True(vm.PreviewPassed);

        // 操作者把版本改成不可用的写法：既不能沿用上一次的 2，也不能发出去。
        vm.TargetVersionText = text;
        paths.Clear();
        await vm.PreviewAsync();
        await vm.PublishAsync();

        Assert.Empty(paths);
        Assert.False(vm.PreviewPassed);
        Assert.False(vm.CanPublish);
        Assert.Contains("目标试卷版本必须是不少于 1 的整数", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_ChangingThePackageAfterPreviewForbidsPublishingTheStaleConclusion()
    {
        var publishes = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/imports/publish")
            {
                publishes++;
            }

            return Task.FromResult(Success(PreviewPassed));
        });
        using var api = Configured(handler);
        var dialogs = new FakeDialogs();
        var vm = new PaperImportViewModel(api, dialogs) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成发布";
        await vm.PreviewAsync();
        Assert.True(vm.CanPublish);

        // 换成另一个材料包：上一次的预检结论不再描述将要发布的包。
        dialogs.Directory = @"C:\synthetic\another-package";
        await vm.SelectPackageAsync();

        Assert.Equal(@"C:\synthetic\another-package", vm.PackageRoot);
        Assert.False(vm.PreviewPassed);
        Assert.False(vm.CanPublish);
        await vm.PublishAsync();
        Assert.Equal(0, publishes);
        Assert.Contains("请先完成预检", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_ChangingTheTargetVersionOrCompilationDirectoryAfterPreviewForbidsPublishing()
    {
        var publishes = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/imports/publish")
            {
                publishes++;
            }

            return Task.FromResult(Success(PreviewPassed));
        });
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成发布";

        vm.TargetVersionText = "2";
        await vm.PreviewAsync();
        vm.TargetVersionText = "3";
        Assert.False(vm.CanPublish);

        await vm.PreviewAsync();
        Assert.True(vm.CanPublish);
        vm.CompilationDirectory = @"C:\synthetic\other-compile";
        Assert.False(vm.CanPublish);

        // 只改发布原因不改变发布对象，因此不作废已通过的预检。
        await vm.PreviewAsync();
        vm.Reason = "合成发布的另一个说法";
        Assert.True(vm.CanPublish);
        await vm.PublishAsync();
        Assert.Equal(1, publishes);
    }

    [Fact]
    public async Task Review_SwitchingExamClearsThePreviousExamPreviewResultAndPaths()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Success(PreviewPassed)));
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成预检";
        await vm.PreviewAsync();
        Assert.True(vm.CanPublish);
        Assert.NotEmpty(vm.Questions);

        vm.ExamId = "synthetic-other-exam";

        // 换考试后不得带着上一场的包路径、预检结论与逐题摘要去发布。
        Assert.False(vm.PreviewPassed);
        Assert.False(vm.CanPublish);
        Assert.Empty(vm.Questions);
        Assert.Empty(vm.Issues);
        Assert.Equal(string.Empty, vm.PackageRoot);
        Assert.Equal(string.Empty, vm.Stage);
        Assert.Contains("尚未核对客户端更新", vm.ClientUpdateSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_PublishFailureAtAnyStageNeverKeepsThePreviewPassedFlag()
    {
        foreach (var stage in new[] { ImportStage.Validate, ImportStage.Package, ImportStage.Store, ImportStage.Publish })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Success(
                request.RequestUri!.AbsolutePath == "/api/v1/imports/publish"
                    ? new ImportPipelineResult(false, stage, [new ImportPipelineIssue("SyntheticStageFailure", "合成阶段失败。")])
                    : PreviewPassed)));
            using var api = Configured(handler);
            var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
            await vm.SelectPackageAsync();
            vm.Reason = "合成发布";
            await vm.PreviewAsync();
            Assert.True(vm.CanPublish);

            await vm.PublishAsync();

            Assert.False(vm.PreviewPassed);
            Assert.False(vm.CanPublish);
            Assert.Equal(stage.ToString(), vm.Stage);
            Assert.Contains("发布未完成", vm.Status, StringComparison.Ordinal);
            Assert.Contains(stage.ToString(), vm.Status, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Review_ASecondPreviewFailureDropsTheEarlierPreviewRowsInsteadOfKeepingThem()
    {
        var fail = false;
        using var handler = new Handler((request, _) => Task.FromResult(Success(
            fail ? PreviewFailed : PreviewPassed)));
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成预检";
        await vm.PreviewAsync();
        Assert.Equal("synthetic-q1", Assert.Single(vm.Questions).QuestionId);

        fail = true;
        await vm.PreviewAsync();

        // 失败不留旧成功状态、不产出部分结果。
        Assert.Empty(vm.Questions);
        Assert.Equal("CompileFailed", Assert.Single(vm.Issues).Code);
        Assert.False(vm.PreviewPassed);
    }

    [Fact]
    public async Task Review_ClientUpdateLookupFailureDoesNotTurnAPublishedResultIntoAFailure()
    {
        using var handler = new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/imports/publish")
            {
                return Task.FromResult(Success(PublishPassed));
            }

            if (path.EndsWith("/versioning/exams/" + ExamId, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "test", null,
                        new ApiError("SYNTHETIC_UNAVAILABLE", "合成故障。")), options: JsonDefaults.Create())
                });
            }

            return Task.FromResult(Success(PreviewPassed));
        });
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成发布";
        await vm.PreviewAsync();

        await vm.PublishAsync();

        // 「发布成功」与「客户端是否已更新」是两件事：核对失败不得改写已成功的发布结果。
        Assert.Contains("试卷版本 2 已生效", vm.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("操作失败", vm.Status, StringComparison.Ordinal);
        Assert.Contains("客户端更新核对失败（发布结果不受影响）", vm.ClientUpdateSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_RefreshClientUpdatesIgnoresParticipantsWhoAlreadyLeftAndWhoseVersionIsAhead()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Success(Snapshot(2) with
        {
            Participants = new Dictionary<string, Exam.Contracts.Versioning.ParticipantVersionStatus>(StringComparer.Ordinal)
            {
                // 版本比目标更高（例如上一场遗留或手工上报）不应算作未更新。
                ["synthetic-ahead"] = new("synthetic-ahead", ParticipationBusinessState.Answering, 2, 3,
                    Exam.Contracts.Versioning.PaperUpdateState.Updated, null),
                ["synthetic-left"] = new("synthetic-left", ParticipationBusinessState.NotParticipating, 2, 0,
                    Exam.Contracts.Versioning.PaperUpdateState.NotReceived, null)
            }
        })));
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };

        await vm.RefreshClientUpdatesAsync();

        Assert.Contains("全部客户端已更新：1 名参试者均已装到版本 2", vm.ClientUpdateSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_PreviewWithoutAPackageOrWithNoReasonNeverReachesTheServer()
    {
        var attempted = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/v1/imports", StringComparison.Ordinal))
            {
                attempted++;
            }

            return Task.FromResult(Success(PreviewPassed));
        });
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };

        // 还没选材料包。
        await vm.PreviewAsync();
        Assert.Equal(0, attempted);
        Assert.Contains("请先选择当前考试与材料包目录", vm.Status, StringComparison.Ordinal);

        await vm.SelectPackageAsync();
        vm.Reason = " ";
        await vm.PreviewAsync();
        Assert.Equal(0, attempted);
        Assert.Contains("请填写服务端编译输出目录与本次发布原因", vm.Status, StringComparison.Ordinal);

        // 取消选择目录不改动既有选择，也不发请求。
        var dialogs = new FakeDialogs { Directory = null };
        var cancelled = new PaperImportViewModel(api, dialogs) { ExamId = ExamId };
        await cancelled.SelectPackageAsync();
        Assert.Equal(string.Empty, cancelled.PackageRoot);
        Assert.Equal(0, attempted);
    }
}
