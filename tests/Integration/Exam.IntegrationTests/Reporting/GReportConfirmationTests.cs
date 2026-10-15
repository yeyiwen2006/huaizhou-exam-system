using Exam.IntegrationTests.Postgres;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Exam.Application.Activation;
using Exam.Application.HighAvailability;
using Exam.Application.Reporting;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Reporting;
using Exam.Reporting;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Submissions;

public sealed partial class PostgresSubmissionStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Reporting_RevalidatesSnapshotAfterDurableConfirmationBeforeSendingSuccess(bool binary, bool invalidate)
    {
        var (context, versions) = await SeedReportExamAsync();
        FinishReportExam(context.ExamId, versions);
        var reports = new ReportPublicationService(ReportSource());
        var status = await reports.StatusAsync(context.ExamId);
        Assert.Equal(ReportState.Ready, status.State);
        var gate = new ReportConfirmationGate();
        await using var app = await ReportConfirmationAppAsync(reports, gate);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReportTestToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Polling has a short client deadline; it must not wait for the report download fence.
        using var statusResponse = await client.GetAsync("/api/v1/reports/" + context.ExamId + "/status", deadline.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Equal(0, gate.Calls);
        var request = client.GetAsync("/api/v1/reports/" + context.ExamId + (binary ? "/xlsx" : "") +
            "?snapshotId=" + status.SnapshotId, deadline.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(deadline.Token);
            Assert.False(request.IsCompleted, "No report success may escape the outstanding confirmation.");
            if (invalidate)
            {
                await SeedParticipationAsync(examId: context.ExamId);
                Assert.Equal(ReportState.Collecting, (await reports.StatusAsync(context.ExamId)).State);
            }
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await request.WaitAsync(deadline.Token);
        Assert.Equal(1, gate.Calls);
        if (invalidate)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.False(response.Headers.Contains("X-Exam-Workbook-Sha256"));
            Assert.Null(response.Content.Headers.ContentDisposition);
            var failure = await response.Content.ReadFromJsonAsync<ApiEnvelope<object>>(JsonDefaults.Create(), deadline.Token);
            Assert.Equal(ErrorCodes.ReportNotReady, failure!.Error!.Code);
            Assert.Null(failure.Result);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (binary)
                Assert.NotEmpty((await ReportWorkbookDownload.ReadAsync(response, status.SnapshotId!, deadline.Token)).Bytes);
            else
            {
                var published = await response.Content.ReadFromJsonAsync<ApiEnvelope<PublishedReport>>(JsonDefaults.Create(), deadline.Token);
                Assert.Equal(status.SnapshotId, published!.Result!.Status.SnapshotId);
            }
        }
    }

    private async Task<WebApplication> ReportConfirmationAppAsync(ReportPublicationService reports, IBusinessConfirmation confirmation)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ExamServer:ServerId"] = "synthetic-report-server";
        builder.Configuration["ExamServer:NodeId"] = "node-a";
        builder.Configuration["ExamServer:ServerEpoch"] = "1";
        builder.Services.ConfigureHttpJsonOptions(options =>
        { foreach (var converter in JsonDefaults.Create().Converters) options.SerializerOptions.Converters.Add(converter); });
        builder.Services.AddSingleton(reports);
        builder.Services.AddSingleton<ReportExportService>();
        builder.Services.AddSingleton(confirmation);
        builder.Services.AddSingleton(new ServerNodeEnvelope("synthetic-report-server", "node-a", 1));
        ReportTestToken = await SyntheticOperators.LoginAsync(fixture.DataSource, "synthetic-operator");
        builder.Services.AddPostgresOperatorAccounts();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddSingleton<IDeviceCredentialValidator, CollectionTestDeviceValidator>();
        builder.Services.AddSingleton(new ActiveNodeGuard(fixture.DataSource,
            new() { ServerId = "synthetic-report-server", NodeId = "node-a", ServerEpoch = 1 }, true));
        var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.UseMiddleware<BusinessConfirmationMiddleware>();
        app.MapGroup("/api/v1").MapReportApi();
        await app.StartAsync();
        return app;
    }

    // This gate tests transport ordering only. It is not a substitute for the native WAL/standby tests.
    private sealed class ReportConfirmationGate : IBusinessConfirmation
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task ConfirmAsync(CancellationToken token)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }
}
