using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Packaging;

namespace Exam.AdminUnitTests;

/// <summary>
/// 试卷导入页在开考后修订上的新表面：变更题号的解析，以及「取回当前版本源材料」落到磁盘。
/// 合成材料，不接触真实试卷；归档是真的加密前明文归档（下载端点返回的就是它）。
/// </summary>
public sealed class ExamImportRevisionViewModelTests
{
    private const string ExamId = "synthetic-revision-exam";
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Theory]
    [InlineData("q1 q2,q3、q4;q1", "q1,q2,q3,q4")]
    [InlineData("  q1 ,, q2  ", "q1,q2")]
    [InlineData("", "")]
    public void ParseChangedQuestions_AcceptsTheDocumentedSeparatorsAndDeduplicates(string text, string expected)
    {
        var vm = new ExamImportViewModel(new AdminApiClient()) { ChangedQuestionsText = text };
        vm.SetCurrentExam(Summary());

        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), vm.ParseChangedQuestions());
    }

    [Fact]
    public void RetrieveMaterialsCommand_IsEnabledOnlyWithACurrentExam()
    {
        var vm = new ExamImportViewModel(new AdminApiClient(), new FakeDialog(string.Empty));

        // 没有当前考试时不可用（避免对着空考试发请求）。
        Assert.False(vm.RetrieveMaterialsCommand.CanExecute(null));

        vm.SetCurrentExam(Summary());
        Assert.True(vm.RetrieveMaterialsCommand.CanExecute(null));

        // 切换考试同样刷新可用状态：命令的 CanExecuteChanged 必须被抬起，否则按钮状态会失真。
        var changed = false;
        vm.RetrieveMaterialsCommand.CanExecuteChanged += (_, _) => changed = true;
        vm.SetCurrentExam(Summary() with { ExamId = "synthetic-other-exam" });
        Assert.True(changed);
        Assert.True(vm.RetrieveMaterialsCommand.CanExecute(null));
    }

    [Fact]
    public async Task RetrieveMaterials_ExtractsTheCurrentVersionArchiveUnderTheChosenParent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "c05-retrieve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var archive = Archive();
            using var handler = new Handler(archive);
            using var api = new AdminApiClient(handler);
            api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
            var vm = new ExamImportViewModel(api, new FakeDialog(parent));
            vm.SetCurrentExam(Summary());

            await vm.RetrieveMaterialsAsync();

            var target = Path.Combine(parent, "合成修订考试-源材料-v1");
            Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(target, "questions", "q1.tex")));
            // 归档自身的清单不写出；取回目录含独立答案，状态里要说明只在本机保存。
            Assert.False(File.Exists(Path.Combine(target, PaperMaterialsArchive.ManifestFileName)));
            Assert.Contains("已取回试卷版本 1", vm.Status, StringComparison.Ordinal);
            Assert.Contains("只在本机保存", vm.Status, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task RetrieveMaterials_WithoutAPublishedVersion_SaysSoAndWritesNothing()
    {
        var parent = Path.Combine(Path.GetTempPath(), "c05-retrieve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            using var handler = new Handler(Archive(), paperVersion: 0);
            using var api = new AdminApiClient(handler);
            api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
            var vm = new ExamImportViewModel(api, new FakeDialog(parent));
            vm.SetCurrentExam(Summary());

            await vm.RetrieveMaterialsAsync();

            Assert.Contains("还没有已发布的试卷版本", vm.Status, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task RetrieveMaterials_WithAnUnreadableArchive_FailsWithAReasonAndNoPartialDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), "c05-retrieve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            using var handler = new Handler([1, 2, 3, 4]);
            using var api = new AdminApiClient(handler);
            api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
            var vm = new ExamImportViewModel(api, new FakeDialog(parent));
            vm.SetCurrentExam(Summary());

            await vm.RetrieveMaterialsAsync();

            Assert.Contains("取回源材料失败", vm.Status, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    private static ExamSummary Summary() => new(ExamId, "合成修订考试",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), ExamLifecycle.Running, false, false, 1, 0, 1);

    private static byte[] Archive() => PaperMaterialsArchive.Build(ExamId, 1,
    [
        new("manifest.json", Encoding.UTF8.GetBytes("{}")),
        new("questions/q1.tex", Encoding.UTF8.GetBytes("\\documentclass{ctexart}")),
        new("server-only/answer-key.json", Encoding.UTF8.GetBytes("{\"answers\":[]}"))
    ]);

    private sealed class FakeDialog(string parent) : IExamImportSourceDialog
    {
        public string? OpenDirectory() => null;
        public string? OpenZip() => null;
        public string? OpenNewMaterialDirectory() => parent;
    }

    private sealed class Handler(byte[] archive, long paperVersion = 1) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/v1/versioning/exams/", StringComparison.Ordinal))
            {
                var snapshot = new ExamVersionSnapshot(ExamId, new VersionVector(1, paperVersion, paperVersion),
                    new ScheduleWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
                    paperVersion > 0 ? ExamLifecycle.Running : ExamLifecycle.Scheduled,
                    null, null, null, null, new Dictionary<string, ParticipantVersionStatus>());
                return Task.FromResult(Reply(snapshot));
            }

            if (path.EndsWith("/paper-materials", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
                response.Headers.Add("X-Exam-Server-Id", "synthetic-server");
                response.Headers.Add("X-Exam-Node-Id", "synthetic-node");
                response.Headers.Add("X-Exam-Server-Epoch", "1");
                response.Headers.Add("X-Exam-Paper-Version", paperVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                response.Headers.Add("X-Exam-Materials-Sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)));
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "test", null,
                    new ApiError("SYNTHETIC_NOT_AVAILABLE", "合成场景没有该记录。")), options: Json)
            });
        }

        private static HttpResponseMessage Reply<T>(T result) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "test", result, null),
                options: Json)
        };
    }
}
