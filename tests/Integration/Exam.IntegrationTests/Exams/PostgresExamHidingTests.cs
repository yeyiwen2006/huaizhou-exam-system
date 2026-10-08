using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Exams;
using Exam.Contracts.Participation;
using Exam.Infrastructure.Postgres;
using Exam.Infrastructure.Postgres.Exams;
using Exam.Infrastructure.Postgres.Student;
using Exam.Infrastructure.Postgres.Versioning;
using Npgsql;

namespace Exam.IntegrationTests.Exams;

public sealed partial class PostgresExamCatalogTests
{
    [Theory]
    [InlineData("scheduled")]
    [InlineData("running")]
    [InlineData("ended")]
    public async Task CatalogHidingScopesPersistIndependentlyAndPreserveStudentAccess(string lifecycle)
    {
        var catalog = Catalog();
        var examId = (await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), Unique("隐藏范围"), Window(), Settings()), "operator-1")).Summary.ExamId;
        var seed = await SeedStudentAsync(examId);
        await catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), true), "operator-1");
        await ExecuteHidingSetupAsync($"UPDATE exam.exam_entry_access SET lifecycle='{lifecycle}' WHERE exam_id=$1", examId);
        var before = await catalog.GetAsync(examId);
        Assert.False(before.Summary.IsHidden);
        var directory = new PostgresStudentDirectory(fixture.DataSource);
        var studentBefore = JsonSerializer.Serialize(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId));

        await catalog.SetHidingAsync(examId, new(Guid.NewGuid().ToString("N"), true, ExamHidingScope.CurrentOperator), "operator-1");
        Assert.True((await catalog.GetAsync(examId, actor: "OPERATOR-1")).Summary.HiddenForCurrentOperator);
        Assert.False((await catalog.GetAsync(examId, actor: "operator-2")).Summary.IsHidden);
        await catalog.SetHidingAsync(examId, new(Guid.NewGuid().ToString("N"), true, ExamHidingScope.AllOperators), "operator-2");

        // Re-open a physical data source and the service; the state must not live in a process cache.
        await using (var reopened = NpgsqlDataSource.Create(fixture.ConnectionString))
        {
            var restarted = new PostgresExamCatalog(reopened, new PostgresExamVersioningService(reopened), TimeProvider.System);
            var personal = (await restarted.ListAsync(actor: "operator-1")).Single(item => item.ExamId == examId);
            Assert.True(personal.HiddenForCurrentOperator);
            Assert.True(personal.HiddenForAllOperators);
            var other = (await restarted.ListAsync(actor: "operator-2")).Single(item => item.ExamId == examId);
            Assert.False(other.HiddenForCurrentOperator);
            Assert.True(other.HiddenForAllOperators);
        }
        await catalog.SetHidingAsync(examId, new(Guid.NewGuid().ToString("N"), false, ExamHidingScope.AllOperators), "operator-2");
        Assert.True((await catalog.GetAsync(examId, actor: "operator-1")).Summary.IsHidden);
        Assert.False((await catalog.GetAsync(examId, actor: "operator-2")).Summary.IsHidden);
        var personalRequest = new SetExamHidingRequest(Guid.NewGuid().ToString("N"), false, ExamHidingScope.CurrentOperator);
        await catalog.SetHidingAsync(examId, personalRequest, "operator-1");
        var after = await catalog.GetAsync(examId, actor: "operator-1");
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(studentBefore, JsonSerializer.Serialize(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId)));
        Assert.Empty(await new PostgresMigrationRunner(fixture.DataSource).MigrateAsync());
    }

    [Fact]
    public async Task HidingRetryCannotReapplyAnOldMutationAndConflictingReuseIsRejected()
    {
        var catalog = Catalog();
        var examId = (await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), Unique("隐藏重试"), Window(), Settings()), "operator-1")).Summary.ExamId;
        var hide = new SetExamHidingRequest(Guid.NewGuid().ToString("N"), true, ExamHidingScope.CurrentOperator);
        await catalog.SetHidingAsync(examId, hide, "operator-1");
        await catalog.SetHidingAsync(examId, hide with { RequestId = Guid.NewGuid().ToString("N"), Hidden = false }, "operator-1");
        Assert.False((await Catalog().SetHidingAsync(examId, hide, "operator-1")).HiddenForCurrentOperator);
        var conflict = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            catalog.SetHidingAsync(examId, hide with { Scope = ExamHidingScope.AllOperators }, "operator-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.IdempotencyConflict, conflict.Code);
    }

    [Fact]
    public async Task HistoricalExamWithoutCatalogRowCanBeHiddenAndRestored()
    {
        var catalog = Catalog();
        var examId = (await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), Unique("历史考试"), Window(), Settings()), "operator-1")).Summary.ExamId;
        await ExecuteHidingSetupAsync("DELETE FROM exam.exam_catalog WHERE exam_id=$1", examId);
        Assert.Contains(await catalog.ListAsync(actor: "operator-1"), item => item.ExamId == examId && !item.IsHidden);
        Assert.True((await catalog.SetHidingAsync(examId, new(Guid.NewGuid().ToString("N"), true, ExamHidingScope.AllOperators), "operator-1")).HiddenForAllOperators);
        Assert.True((await catalog.GetAsync(examId, actor: "operator-2")).Summary.IsHidden);
        Assert.False((await catalog.SetHidingAsync(examId, new(Guid.NewGuid().ToString("N"), false, ExamHidingScope.AllOperators), "operator-2")).HiddenForAllOperators);
    }

    [Fact]
    public async Task HidingRejectsInvalidScopeAndUnknownExam()
    {
        var catalog = Catalog();
        var invalid = await Assert.ThrowsAsync<ParticipationRuleException>(() => catalog.SetHidingAsync("missing",
            new(Guid.NewGuid().ToString("N"), true, (ExamHidingScope)999), "operator-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.InvalidRequest, invalid.Code);
        var missing = await Assert.ThrowsAsync<ParticipationRuleException>(() => catalog.SetHidingAsync("missing",
            new(Guid.NewGuid().ToString("N"), true, ExamHidingScope.CurrentOperator), "operator-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.ExamNotFound, missing.Code);
    }

    private async Task ExecuteHidingSetupAsync(string sql, string examId)
    {
        await using var command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(examId);
        await command.ExecuteNonQueryAsync();
    }
}
