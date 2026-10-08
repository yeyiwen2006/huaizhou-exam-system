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
    [Fact]
    public Task SingleChoiceMaximumMismatch_PreventsSavingBeforeAnApiRequest() => OnStaAsync(async () =>
    {
        var requests = 0;
        using var api = Configured(new Handler(_ =>
        {
            requests++;
            throw new InvalidOperationException("Invalid settings must not be sent.");
        }));
        var draft = new ExamConfigurationViewModel(api);
        draft.ResetForNew();
        draft.Name = "合成满分校验考试";
        var group = draft.Subjects[0].Groups[0];
        group.QuestionCount = 10;
        group.Correct = "2";
        group.MaxScore = "19";

        await draft.SaveAsync();

        Assert.Equal(0, requests);
        Assert.Contains("单项选择题满分不一致", draft.ValidationSummary);
        Assert.Contains("10 × 每小题选对得分 2 = 20", draft.ValidationSummary);
        Assert.Contains("填写内容有误", draft.Status);
        group.MaxScore = "20";
        Assert.Empty(draft.Validate());
    });

    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private static readonly DateTimeOffset Start = new(2027, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    [Trait("Category", "Wpf")]
    public Task Home_ListsExamsByNameAndOpeningAnExamScopesEveryPage() => OnStaAsync(async () =>
    {
        var summary = Summary("exam-internal-1", "合成模拟考试", visible: true) with { PaperVersion = 2 };
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
        Assert.True(card.VisibleToStudents);
        Assert.Equal("未开始", card.StateText);
        Assert.Equal("试卷已发布", card.PublicationText);

        card.OpenCommand.Execute(null);
        await main.Exam.OpenAsync(summary.ExamId);
        Assert.Equal("合成模拟考试", main.Exam.Name);
        Assert.Equal(summary.ExamId, main.Exam.ExamId);
        Assert.Equal(summary.ExamId, main.Monitoring.ExamId);
        Assert.Equal(summary.ExamId, main.Reports.ExamId);
        Assert.Equal("合成模拟考试", main.Reports.ExamName);
        Assert.Equal("合成数学", Assert.Single(main.Exam.Subjects).Name);
        Assert.True(main.Exam.VisibleToStudents);
        Assert.Equal("试卷已发布", main.Import.PublicationText);
        summary = summary with { PaperVersion = 3 };
        await main.Import.RefreshPublicationAsync();
        Assert.Equal(3, main.Import.PaperVersion);
        Assert.Equal(3, main.CurrentExam!.PaperVersion);
        Assert.Equal(3, Assert.Single(main.Home.Exams).Exam.PaperVersion);
    });

    [Theory]
    [InlineData(0L, "试卷未发布")]
    [InlineData(2L, "试卷已发布")]
    [InlineData(null, "发布状态未确认")]
    public void PublicationStateIsIndependentOfVisibility(long? version, string expected)
    {
        var summary = Summary("exam", "合成考试", visible: true) with { PaperVersion = version };
        var card = new ExamCardViewModel(summary, () => { });
        using var api = new AdminApiClient();
        using var import = new ExamImportViewModel(api);
        import.SetCurrentExam(summary);
        Assert.Equal(expected, card.PublicationText);
        Assert.Equal(expected, import.PublicationText);
        import.SourcePath = "synthetic-new-preview";
        Assert.Equal(expected, import.PublicationText);
    }

    [Theory]
    [InlineData(ExamLifecycle.Scheduled)]
    [InlineData(ExamLifecycle.Running)]
    [InlineData(ExamLifecycle.Ended)]
    public Task ExamNameCanBeSavedInEveryLifecycleWithoutChangingTheStructure(ExamLifecycle lifecycle) => OnStaAsync(async () =>
    {
        var summary = Summary("exam", "原考试名称", visible: true) with { Lifecycle = lifecycle, PaperVersion = 1 };
        var original = Detail(summary);
        UpdateExamSettingsRequest? posted = null;
        using var api = Configured(new Handler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                posted = request.Content!.ReadFromJsonAsync<UpdateExamSettingsRequest>(Json).Result;
                summary = summary with { Name = posted!.Name, SettingsVersion = 2 };
            }
            return Success(Detail(summary));
        }));
        var vm = new ExamConfigurationViewModel(api);
        await vm.OpenAsync("exam");
        Assert.True(vm.IsNameEditable);
        Assert.Equal(lifecycle == ExamLifecycle.Scheduled, vm.IsSettingsEditable);
        vm.Name = "更名后的考试";
        vm.RefreshInstructionsCommand.Execute(null);
        Assert.Equal(original.InstructionLines, vm.InstructionPreview.Select(line => line.Text));
        await vm.SaveAsync();
        Assert.Equal("更名后的考试", posted!.Name);
        Assert.Equal(JsonSerializer.Serialize(original.Settings, Json), JsonSerializer.Serialize(posted.Settings, Json));
        Assert.Equal("exam", vm.ExamId);
        Assert.Equal("更名后的考试", vm.Name);
    });

    [Theory]
    [InlineData(ExamLifecycle.Scheduled, true)]
    [InlineData(ExamLifecycle.Running, false)]
    [InlineData(ExamLifecycle.Ended, true)]
    public async Task HomeVisibilityUsesLifecycleEvenWhenUncollectedAnswersRemain(ExamLifecycle lifecycle, bool canHide)
    {
        var summary = Summary("exam", "显示规则检查", true, locked: true) with { Lifecycle = lifecycle };
        using var api = Configured(new Handler(request => request.Method == HttpMethod.Get
            ? Success(new[] { summary }) : Success(summary with { VisibleToStudents = false })));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var card = Assert.Single(home.Exams);
        Assert.Equal(canHide, card.CanChangeVisibility);
        card.VisibleToStudents = false;
        await WaitAsync(() => !card.IsVisibilitySaving);
        Assert.Equal(!canHide, card.VisibleToStudents);
    }

    [Fact]
    public async Task HomeVisibilityToggleImmediatelySendsOnlyTheVisibilityRequest()
    {
        var summary = Summary("exam", "合成考试", visible: true);
        SetExamVisibilityRequest? posted = null;
        using var api = Configured(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Success(new[] { summary });
            Assert.Equal("/api/v1/exams/exam/visibility", request.RequestUri!.AbsolutePath);
            posted = request.Content!.ReadFromJsonAsync<SetExamVisibilityRequest>(Json).Result;
            return Success(summary with { VisibleToStudents = posted!.VisibleToStudents });
        }));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var card = Assert.Single(home.Exams);
        card.VisibleToStudents = false;
        await WaitAsync(() => !card.IsVisibilitySaving);
        Assert.NotNull(posted);
        Assert.False(posted!.VisibleToStudents);
        Assert.False(card.Exam.VisibleToStudents);
        Assert.Contains("已设置", home.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HomeVisibilityLockAndServerRefusalKeepTheConfirmedValue(bool locked)
    {
        var puts = 0;
        using var api = Configured(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Success(new[] { Summary("exam", "合成考试", true, locked) });
            puts++;
            return Error(HttpStatusCode.Conflict, ExamCatalogErrorCodes.VisibilityLocked);
        }));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var card = Assert.Single(home.Exams);
        Assert.Equal(!locked, card.CanChangeVisibility);
        card.VisibleToStudents = false;
        await WaitAsync(() => !card.IsVisibilitySaving);
        Assert.True(card.VisibleToStudents);
        Assert.True(card.Exam.VisibleToStudents);
        Assert.Equal(locked ? 0 : 1, puts);
    }

    [Fact]
    public async Task DelayedExamListDoesNotOverwriteANewerVisibilityReceipt()
    {
        var original = Summary("exam", "合成考试", visible: false);
        var pendingList = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listCalls = 0;
        using var api = Configured(new AsyncHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return ++listCalls == 1 ? Task.FromResult(Success(new[] { original })) : pendingList.Task;
            return Task.FromResult(Success(original with { VisibleToStudents = true, SettingsVersion = 2 }));
        }));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var oldRefresh = home.RefreshAsync();
        var card = Assert.Single(home.Exams);
        card.VisibleToStudents = true;
        await WaitAsync(() => !card.IsVisibilitySaving);
        pendingList.SetResult(Success(new[] { original }));
        await oldRefresh;
        var current = Assert.Single(home.Exams);
        Assert.True(current.VisibleToStudents);
        Assert.Equal(2, current.Exam.SettingsVersion);
        ExamSummary? opened = null;
        home.ExamOpened += summary => opened = summary;
        current.OpenCommand.Execute(null);
        Assert.True(opened!.VisibleToStudents);
    }

    [Theory]
    [InlineData(2L, false)]
    [InlineData(2L, true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task DelayedVisibilityReceiptPreservesNewerPublicationState(long? publishedVersion, bool replaceCard)
    {
        var original = Summary("exam", "合成考试", visible: false) with { PaperVersion = 1 };
        var pendingVisibility = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingList = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lists = 0;
        using var api = Configured(new AsyncHandler(request => request.Method == HttpMethod.Get
            ? ++lists == 1 ? Task.FromResult(Success(new[] { original })) : pendingList.Task
            : pendingVisibility.Task));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var oldCard = Assert.Single(home.Exams);
        oldCard.VisibleToStudents = true;
        Assert.True(oldCard.IsVisibilitySaving);
        var refresh = replaceCard ? home.RefreshAsync() : Task.CompletedTask;
        home.UpdatePaperVersion("exam", publishedVersion);
        if (replaceCard)
        {
            pendingList.SetResult(Success(new[] { original }));
            await refresh;
        }
        pendingVisibility.SetResult(Success(original with { VisibleToStudents = true, SettingsVersion = 2 }));
        await WaitAsync(() => !oldCard.IsVisibilitySaving);
        var current = Assert.Single(home.Exams);
        Assert.Equal(publishedVersion, current.Exam.PaperVersion);
        Assert.Equal(publishedVersion, oldCard.Exam.PaperVersion);
        Assert.True(current.VisibleToStudents);
        Assert.Equal(2, current.Exam.SettingsVersion);
        ExamSummary? opened = null;
        home.ExamOpened += summary => opened = summary;
        current.OpenCommand.Execute(null);
        Assert.Equal(publishedVersion, opened!.PaperVersion);
    }

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

    internal static async Task OnStaAsync(Func<Task> test)
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
        new(id, name, Start, Start.AddHours(2), locked ? ExamLifecycle.Running : ExamLifecycle.Scheduled, visible, locked, locked ? 2 : 0, locked ? 1 : 0, 1);

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
            new ApiError(code, "考试进行中不能取消显示；考试结束后可以取消显示。")), options: Json)
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
