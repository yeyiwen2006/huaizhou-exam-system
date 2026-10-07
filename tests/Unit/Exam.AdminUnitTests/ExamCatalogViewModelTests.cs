using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;

namespace Exam.AdminUnitTests;

/// <summary>主页考试卡片、新建考试、按名称进入考试与学生端显示开关。全部为合成数据。</summary>
public sealed class ExamCatalogViewModelTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private static readonly DateTimeOffset Start = new(2027, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    [Trait("Category", "Wpf")]
    public Task Home_ListsExamsByNameAndOpeningAnExamScopesEveryPage() => OnStaAsync(async () =>
    {
        var summary = Summary("exam-internal-1", "合成模拟考试", visible: true);
        var api = Configured(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/exams" => Success(new[] { summary }),
            "/api/v1/exams/exam-internal-1" => Success(Detail(summary)),
            _ => Error(HttpStatusCode.NotFound, "NOT_FOUND"),
        }));
        var main = new MainViewModel(api);
        await main.Home.RefreshAsync();
        var card = Assert.Single(main.Home.Exams);
        Assert.Equal("合成模拟考试", card.Name);
        Assert.Equal("学生端可见", card.VisibilityText);
        Assert.Equal("未开始", card.StateText);

        card.OpenCommand.Execute(null);
        await main.Exam.OpenAsync(summary.ExamId);
        Assert.Equal("合成模拟考试", main.Exam.Name);
        Assert.Equal(summary.ExamId, main.Exam.ExamId);
        Assert.Equal(summary.ExamId, main.Monitoring.ExamId);
        Assert.Equal(summary.ExamId, main.Reports.ExamId);
        Assert.Equal("合成模拟考试", main.Reports.ExamName);
        Assert.Equal("合成数学", Assert.Single(main.Exam.Subjects).Name);
        Assert.True(main.Exam.VisibleToStudents);
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task NewExam_PostsNameScheduleAndStructuredSettings_ThenBecomesCurrentExam() => OnStaAsync(async () =>
    {
        CreateExamRequest? posted = null;
        var created = Summary("exam-new", "新建合成考试", visible: false);
        var api = Configured(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/exams")
            {
                posted = request.Content!.ReadFromJsonAsync<CreateExamRequest>(Json).Result;
                return Success(Detail(created));
            }
            return request.RequestUri!.AbsolutePath == "/api/v1/exams" ? Success(new[] { created }) : Error(HttpStatusCode.NotFound, "NOT_FOUND");
        }));
        var main = new MainViewModel(api);
        main.NewExam();
        Assert.True(main.Exam.IsNewExam);
        Assert.True(main.CanEditSettings);
        Assert.False(main.HasCurrentExam);
        main.Exam.Name = "新建合成考试";
        Assert.EndsWith(" 12:00", main.Exam.ScheduledEndText);
        var subject = Assert.Single(main.Exam.Subjects);
        var group = Assert.Single(subject.Groups);
        Assert.Equal("0", subject.MaxScore);
        Assert.Equal(0, group.QuestionCount);
        Assert.Equal("0", group.MaxScore);
        subject.MaxScore = group.MaxScore = "10";
        group.QuestionCount = 10;
        await main.Exam.SaveAsync();

        Assert.NotNull(posted);
        Assert.Equal("新建合成考试", posted!.Name);
        Assert.False(string.IsNullOrWhiteSpace(posted.RequestId));
        Assert.NotEmpty(posted.Settings.Subjects);
        Assert.False(main.Exam.IsNewExam);
        Assert.Equal("exam-new", main.CurrentExam!.ExamId);
        Assert.Equal("新建合成考试", main.CurrentExamTitle);
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task NewExam_InvalidNameIsNotSent() => OnStaAsync(async () =>
    {
        var calls = 0;
        var api = Configured(new Handler(_ => { calls++; return Error(HttpStatusCode.InternalServerError, "X"); }));
        var main = new MainViewModel(api);
        main.NewExam();
        main.Exam.Name = "名称?";
        await main.Exam.SaveAsync();
        Assert.Equal(0, calls);
        Assert.Contains("填写内容有误", main.Exam.Status, StringComparison.Ordinal);
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task Visibility_LockedExamCannotBeHidden_AndServerRefusalKeepsItShown() => OnStaAsync(async () =>
    {
        var summary = Summary("exam-locked", "已有考生的考试", visible: true, locked: true);
        var api = Configured(new Handler(request => request.Method == HttpMethod.Put
            ? Error(HttpStatusCode.Conflict, ExamCatalogErrorCodes.VisibilityLocked)
            : Success(Detail(summary))));
        var main = new MainViewModel(api);
        await main.Exam.OpenAsync(summary.ExamId);
        Assert.True(main.Exam.VisibleToStudents);
        Assert.False(main.Exam.CanChangeVisibility);
        Assert.Contains("不能取消", main.Exam.VisibilityNote, StringComparison.Ordinal);

        main.Exam.VisibleToStudents = false;
        await WaitAsync(() => main.Exam.Status.StartsWith("显示设置未修改", StringComparison.Ordinal));
        Assert.True(main.Exam.VisibleToStudents);
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task Visibility_CanBeTurnedOnForUnlockedExam() => OnStaAsync(async () =>
    {
        var summary = Summary("exam-open", "可显示的考试", visible: false);
        var api = Configured(new Handler(request => request.Method == HttpMethod.Put
            ? Success(summary with { VisibleToStudents = true })
            : Success(Detail(summary))));
        var main = new MainViewModel(api);
        await main.Exam.OpenAsync(summary.ExamId);
        Assert.True(main.Exam.CanChangeVisibility);
        main.Exam.VisibleToStudents = true;
        await WaitAsync(() => main.Exam.VisibleToStudents);
        Assert.Contains("学生登录后", main.Exam.VisibilityNote, StringComparison.Ordinal);
    });

    private static async Task OnStaAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            });
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static ExamSummary Summary(string id, string name, bool visible, bool locked = false) =>
        new(id, name, Start, Start.AddHours(2), ExamLifecycle.Scheduled, visible, locked, locked ? 2 : 0, locked ? 1 : 0, 1);

    private static ExamDetail Detail(ExamSummary summary) => new(summary, new ExamSettings(
    [
        new ExamSubjectSettings("math", "合成数学", "6",
            [new ExamGroupSettings("math-1", "一、单项选择题", "single", 1, 2, "6", false, new ManifestOneCorrect("3", "0", "0"), null)]),
    ]), 1, ["合成数学（满分6分）"]);

    private static AdminApiClient Configured(HttpMessageHandler handler)
    {
        var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        return api;
    }

    private static HttpResponseMessage Success<T>(T result) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>("server-1", "node-1", 1, "catalog-test", result, null), options: Json)
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
    {
        Content = JsonContent.Create(new ApiEnvelope<object>("server-1", "node-1", 1, "catalog-test", null,
            new ApiError(code, "已有考生确认参试，全部实际参试考生的答卷回收完成前，不能取消在学生端显示。")), options: Json)
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
