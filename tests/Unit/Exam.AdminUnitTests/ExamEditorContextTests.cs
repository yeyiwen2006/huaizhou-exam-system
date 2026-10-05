using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;

namespace Exam.AdminUnitTests;

public sealed class ExamEditorContextTests
{
    [Fact]
    public async Task FailedOpenRevokesOldIdentityAndEverySaveBeforeFirstAwait()
    {
        var pending = NewResponse();
        var writes = new ConcurrentQueue<string>();
        using var api = Client(request =>
        {
            if (request.Method != HttpMethod.Get) writes.Enqueue(request.RequestUri!.AbsolutePath);
            return request.RequestUri!.AbsolutePath.EndsWith("/B", StringComparison.Ordinal)
                ? pending.Task : Task.FromResult(Ok(Detail("A")));
        });
        var vm = new ExamConfigurationViewModel(api);
        await vm.OpenAsync("A");
        Assert.True(vm.CanSave);
        var opening = vm.OpenAsync("B");
        Assert.Empty(vm.ExamId);
        Assert.Empty(vm.Name);
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.SaveAsync();
        pending.SetResult(Fail(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<AdminApiException>(() => opening);
        await vm.SaveAsync();
        Assert.Empty(writes);
        Assert.Empty(vm.ExamId);
        Assert.True(vm.ReloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task OlderReadFailureCannotOverwriteNewExamOrItsStatus()
    {
        var pending = NewResponse();
        using var api = Client(request => request.RequestUri!.AbsolutePath.EndsWith("/A", StringComparison.Ordinal)
            ? pending.Task : Task.FromResult(Ok(Detail("B"))));
        var vm = new ExamConfigurationViewModel(api);
        var first = vm.OpenAsync("A");
        await vm.OpenAsync("B");
        var status = vm.Status;
        pending.SetResult(Fail(HttpStatusCode.BadGateway));
        await first;
        Assert.Equal("B", vm.ExamId);
        Assert.Equal(status, vm.Status);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task WrongExamResponseCannotEnableSaving()
    {
        using var api = Client(_ => Task.FromResult(Ok(Detail("another-exam"))));
        var vm = new ExamConfigurationViewModel(api);
        // 边界读取现在在客户端返回前整体校验（含考试身份），因此在页面使用前即以 AdminApiException 拒绝。
        await Assert.ThrowsAsync<AdminApiException>(() => vm.OpenAsync("requested-exam"));
        Assert.False(vm.CanSave);
        Assert.Empty(vm.ExamId);
    }

    [Fact]
    public async Task SwitchingToNewExamWhileSavingStopsOldFollowupWrites()
    {
        var pending = NewResponse();
        var writes = new ConcurrentQueue<string>();
        using var api = Client(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                writes.Enqueue(request.RequestUri!.AbsolutePath);
                return pending.Task;
            }
            return Task.FromResult(Ok(Detail("A")));
        });
        var vm = new ExamConfigurationViewModel(api);
        await vm.OpenAsync("A");
        vm.ScheduledEndText = Detail("A").Summary.EndsAt.AddHours(1).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var saving = vm.SaveAsync();
        Assert.False(vm.CanSave);
        vm.ResetForNew();
        vm.Name = "新的合成考试";
        pending.SetResult(Ok(Detail("A", settingsVersion: 2)));
        await saving;
        Assert.Equal("/api/v1/exams/A/settings", Assert.Single(writes));
        Assert.True(vm.IsNewExam);
        Assert.Equal("新的合成考试", vm.Name);
        Assert.Empty(vm.ExamId);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task RunningExamDisablesStructureCommandsButKeepsTimeEditable()
    {
        using var api = Client(_ => Task.FromResult(Ok(Detail("A", ExamLifecycle.Running))));
        var vm = new ExamConfigurationViewModel(api);
        await vm.OpenAsync("A");
        Assert.False(vm.IsSettingsEditable);
        Assert.False(vm.AddSubjectCommand.CanExecute(null));
        Assert.False(vm.RemoveSubjectCommand.CanExecute(null));
        Assert.False(vm.AddGroupCommand.CanExecute(null));
        Assert.False(vm.RemoveGroupCommand.CanExecute(null));
        Assert.True(vm.CanEditTime);
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.AddSubjectCommand.Execute(null);
        vm.RemoveGroupCommand.Execute(null);
        Assert.Single(vm.Subjects);
        Assert.Single(vm.Subjects[0].Groups);
    }

    [Fact]
    public async Task ScheduleConflictAfterSettingsCommitIsNotReportedAsAllSaved()
    {
        var writes = new ConcurrentQueue<string>();
        using var api = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                writes.Enqueue(path);
                return Task.FromResult(path.EndsWith("/schedule", StringComparison.Ordinal)
                    ? Fail(HttpStatusCode.Conflict) : Ok(Detail("A", settingsVersion: 2)));
            }
            return Task.FromResult(Ok(Detail("A")));
        });
        var vm = new ExamConfigurationViewModel(api);
        await vm.OpenAsync("A");
        vm.ScheduledEndText = Detail("A").Summary.EndsAt.AddHours(1).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        await Assert.ThrowsAsync<AdminApiException>(() => vm.SaveAsync());
        Assert.Equal(2, writes.Count);
        Assert.Contains("部分请求已完成", vm.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("考试设置已保存", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.CanSave);
        Assert.True(vm.ReloadCommand.CanExecute(null));
        await vm.SaveAsync();
        Assert.Equal(2, writes.Count);
    }

    [Fact]
    public async Task ConnectionResetDuringReadLeavesNoWritableOldEditor()
    {
        var pending = NewResponse();
        using var api = Client(_ => pending.Task);
        var vm = new ExamConfigurationViewModel(api);
        var loading = vm.OpenAsync("A");
        api.Disconnect();
        pending.TrySetResult(Ok(Detail("A")));
        await loading;
        Assert.Empty(vm.ExamId);
        Assert.Empty(vm.Subjects);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task ManuallyChangingIdentityCannotReuseLoadedSettingsForAnotherExam()
    {
        using var api = Client(_ => Task.FromResult(Ok(Detail("A"))));
        var vm = new ExamConfigurationViewModel(api);
        await vm.OpenAsync("A");
        vm.ExamId = "B";
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    private static ExamDetail Detail(string id, ExamLifecycle lifecycle = ExamLifecycle.Scheduled, long settingsVersion = 1)
    {
        var start = new DateTimeOffset(2027, 10, 2, 9, 0, 0, TimeSpan.FromHours(8));
        return new(new(id, "合成考试" + id, start, start.AddHours(2), lifecycle, false, false, 0, 0, settingsVersion),
            new ExamSettings([new ExamSubjectSettings("subject", "合成科目", "1",
                [new ExamGroupSettings("group", "单项选择", "single", 1, 1, "1", false,
                    new ManifestOneCorrect("1", "0", "0"), null)])]), 1, ["合成说明"]);
    }

    private static TaskCompletionSource<HttpResponseMessage> NewResponse() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AdminApiClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var api = new AdminApiClient(new Handler(respond));
        api.Configure("https://localhost/", "synthetic-session", "synthetic-operator");
        return api;
    }
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "context-test", value, null), options: JsonDefaults.Create()) };
    private static HttpResponseMessage Fail(HttpStatusCode status) => new(status)
    { Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "context-test", null,
        new ApiError("SYNTHETIC_FAILURE", "合成失败，请重新读取。")), options: JsonDefaults.Create()) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
