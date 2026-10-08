using System.Net;
using System.Net.Http.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Operators;
using Exam.Server.Api;
using Microsoft.AspNetCore.TestHost;

namespace Exam.IntegrationTests.Api;

public sealed partial class OperatorAccountApiTests
{
    [Fact]
    public async Task ExamHidingUsesAuthenticatedIdentityAndSynchronizesOnlyTheChosenScope()
    {
        await ResetAccountsAsync();
        await using var app = await AppAsync();
        using var anonymous = app.GetTestClient();
        await RegisterAsync(anonymous, "admin-one", AdminPassword);
        await RegisterAsync(anonymous, "staff-one", StaffPassword);
        using var admin = await LoggedInClientAsync(app, "admin-one", AdminPassword);
        await SetStatusAsync(admin, "staff-one", OperatorStatus.Active);
        using var staff = await LoggedInClientAsync(app, "staff-one", StaffPassword);
        var request = new CreateExamRequest(Guid.NewGuid().ToString("N"), "合成隐藏接口考试-" + Guid.NewGuid().ToString("N"),
            new(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(1).AddHours(2)),
            new([new("math", "合成科目", "2", [new("single", "单选", "single", 1, 1, "2", false, new("2", "0", "0"), null)])]));
        using var created = await admin.PostAsJsonAsync("/api/v1/exams", request, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var examId = (await created.Content.ReadFromJsonAsync<ApiEnvelope<ExamDetail>>(Json))!.Result!.Summary.ExamId;
        var path = $"/api/v1/exams/{examId}/hiding";
        var mine = new SetExamHidingRequest(Guid.NewGuid().ToString("N"), true, ExamHidingScope.CurrentOperator);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, mine, Json)).StatusCode);
        anonymous.DefaultRequestHeaders.Add("X-Exam-Device-Id", "synthetic-device");
        anonymous.DefaultRequestHeaders.Add("X-Exam-Device-Credential", "synthetic-device-credential");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, mine, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync(path,
            new { requestId = Guid.NewGuid().ToString("N"), hidden = true, scope = "currentOperator", operatorId = "admin-one" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync(path, mine, Json)).StatusCode);
        var personal = (await staff.GetFromJsonAsync<ApiEnvelope<ExamSummary[]>>("/api/v1/exams", Json))!.Result!.Single(item => item.ExamId == examId);
        Assert.True(personal.HiddenForCurrentOperator);
        var other = (await admin.GetFromJsonAsync<ApiEnvelope<ExamDetail>>($"/api/v1/exams/{examId}", Json))!.Result!.Summary;
        Assert.False(other.IsHidden);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(path,
            mine with { RequestId = Guid.NewGuid().ToString("N"), Scope = ExamHidingScope.AllOperators }, Json)).StatusCode);
        var both = (await staff.GetFromJsonAsync<ApiEnvelope<ExamDetail>>($"/api/v1/exams/{examId}", Json))!.Result!.Summary;
        Assert.True(both.HiddenForAllOperators);
        Assert.True(both.HiddenForCurrentOperator);
        Assert.False(both.VisibleToStudents);
    }
}
