using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Admin.ViewModels;
using Exam.Contracts.Exams;

namespace Exam.AdminUnitTests;

public sealed partial class ExamCatalogViewModelTests
{
    [Theory]
    [InlineData(ExamHidingScope.CurrentOperator)]
    [InlineData(ExamHidingScope.AllOperators)]
    public async Task HidingMovesCardsOnlyAfterConfirmationAndCanBeUndone(ExamHidingScope scope)
    {
        var summary = Summary("exam", "合成隐藏考试", true) with { PaperVersion = 3 };
        var requests = new List<SetExamHidingRequest>();
        using var api = Configured(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Success(new[] { summary });
            Assert.Equal("/api/v1/exams/exam/hiding", request.RequestUri!.AbsolutePath);
            var input = request.Content!.ReadFromJsonAsync<SetExamHidingRequest>(Json).Result!;
            requests.Add(input);
            Assert.NotEmpty(input.RequestId);
            Assert.Equal(scope, input.Scope);
            summary = scope == ExamHidingScope.CurrentOperator
                ? summary with { HiddenForCurrentOperator = input.Hidden }
                : summary with { HiddenForAllOperators = input.Hidden };
            return Success(new ExamHidingState("exam", summary.HiddenForCurrentOperator, summary.HiddenForAllOperators));
        }));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var card = Assert.Single(home.Exams);
        var opens = 0;
        home.ExamOpened += _ => opens++;
        var command = scope == ExamHidingScope.CurrentOperator ? card.ChangePersonalHidingCommand : card.ChangeSharedHidingCommand;
        command.Execute(null);
        await WaitAsync(() => !card.IsHidingSaving);
        Assert.Empty(home.Exams);
        Assert.Same(card, Assert.Single(home.HiddenExams));
        Assert.Equal("取消隐藏", card.HidingActionText);
        Assert.True(card.VisibleToStudents);
        Assert.Equal(3, card.Exam.PaperVersion);
        Assert.Equal(0, opens);
        card.OpenCommand.Execute(null);
        Assert.Equal(1, opens);
        command.Execute(null);
        await WaitAsync(() => !card.IsHidingSaving);
        Assert.Empty(home.HiddenExams);
        Assert.Same(card, Assert.Single(home.Exams));
        Assert.Equal("隐藏该考试", card.HidingActionText);
        Assert.Equal(new[] { true, false }, requests.Select(item => item.Hidden));
        await home.RefreshAsync();
        Assert.False(Assert.Single(home.Exams).Exam.IsHidden);
    }

    [Fact]
    public async Task CancellingOneScopePreservesTheOtherScope()
    {
        var summary = Summary("exam", "双范围隐藏", true) with { HiddenForCurrentOperator = true, HiddenForAllOperators = true };
        using var api = Configured(new Handler(request => request.Method == HttpMethod.Get ? Success(new[] { summary })
            : Success(new ExamHidingState("exam", false, true))));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var card = Assert.Single(home.HiddenExams);
        card.ChangePersonalHidingCommand.Execute(null);
        await WaitAsync(() => !card.IsHidingSaving);
        Assert.Same(card, Assert.Single(home.HiddenExams));
        Assert.Empty(home.Exams);
        Assert.Contains("所有考务账号", card.HidingScopeText, StringComparison.Ordinal);
        Assert.Contains("另一范围", home.Status, StringComparison.Ordinal);
        Assert.False(card.ChangePersonalHidingCommand.CanExecute(null));
        Assert.True(card.ChangeSharedHidingCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrMismatchedHidingReceiptKeepsTheConfirmedCard(bool mismatched)
    {
        var summary = Summary("exam", "合成考试", true);
        using var api = Configured(new Handler(request => request.Method == HttpMethod.Get ? Success(new[] { summary })
            : mismatched ? Success(new ExamHidingState("other", true, false)) : Error(HttpStatusCode.BadRequest, "INVALID_REQUEST")));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var card = Assert.Single(home.Exams);
        card.ChangePersonalHidingCommand.Execute(null);
        await WaitAsync(() => !card.IsHidingSaving);
        Assert.Same(card, Assert.Single(home.Exams));
        Assert.Empty(home.HiddenExams);
        Assert.False(card.Exam.IsHidden);
        Assert.Contains("未确认", home.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelayedListDoesNotUndoANewerHidingReceipt()
    {
        var summary = Summary("exam", "合成考试", true);
        var pendingList = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var api = Configured(new AsyncHandler(request => request.Method == HttpMethod.Get
            ? ++calls == 1 ? Task.FromResult(Success(new[] { summary })) : pendingList.Task
            : Task.FromResult(Success(new ExamHidingState("exam", true, false)))));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var refresh = home.RefreshAsync();
        var card = Assert.Single(home.Exams);
        card.ChangePersonalHidingCommand.Execute(null);
        await WaitAsync(() => !card.IsHidingSaving);
        pendingList.SetResult(Success(new[] { summary }));
        await refresh;
        Assert.Empty(home.Exams);
        Assert.True(Assert.Single(home.HiddenExams).Exam.HiddenForCurrentOperator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingHidingPreservesRefreshedFieldsAndCannotLeakAcrossAccounts(bool switchAccount)
    {
        var summary = Summary("exam", "合成考试", false);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var puts = 0;
        using var api = Configured(new AsyncHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Success(new[] { summary }));
            puts++;
            return pending.Task;
        }));
        var home = new ExamHomeViewModel(api);
        await home.RefreshAsync();
        var oldCard = Assert.Single(home.Exams);
        oldCard.ChangePersonalHidingCommand.Execute(null);
        Assert.True(oldCard.IsHidingSaving);
        Assert.Same(oldCard, Assert.Single(home.Exams));
        Assert.Empty(home.HiddenExams);
        if (switchAccount) api.Configure("https://localhost:5001/", "synthetic-other-token", "operator-2");
        summary = summary with { VisibleToStudents = true, Name = "刷新后的名称" };
        await home.RefreshAsync();
        var current = Assert.Single(home.Exams);
        Assert.Equal(!switchAccount, current.IsHidingSaving);
        if (!switchAccount)
        {
            current.ChangeSharedHidingCommand.Execute(null);
            Assert.False(current.CanChangeVisibility);
        }
        home.UpdatePaperVersion("exam", 5);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldCard.ChangePersonalHidingCommand.CanExecuteChanged += (_, _) =>
        {
            if (switchAccount || !current.IsHidingSaving) completed.TrySetResult();
        };
        pending.SetResult(Success(new ExamHidingState("exam", true, false)));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, puts);
        Assert.Equal("刷新后的名称", current.Name);
        Assert.Equal(5, current.Exam.PaperVersion);
        Assert.True(current.VisibleToStudents);
        if (switchAccount) { Assert.Empty(home.HiddenExams); Assert.False(current.Exam.IsHidden); }
        else { Assert.Empty(home.Exams); Assert.Same(current, Assert.Single(home.HiddenExams)); }
    }
}
