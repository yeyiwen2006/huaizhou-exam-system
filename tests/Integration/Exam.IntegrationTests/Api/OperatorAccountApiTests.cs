using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Api;

/// <summary>
/// 考务账号：经正式中间件与接口验证注册、审批、登录、停用和退出登录。全部为合成账号与口令。
/// 同一集合内的 PostgreSQL 测试顺序执行，本测试开始时清空账号表以验证“第一个账号为管理员”。
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class OperatorAccountApiTests(PostgresDatabaseFixture fixture)
{
    private const string AdminPassword = "synthetic-admin-password";
    private const string StaffPassword = "synthetic-staff-password";
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public async Task FirstAccountIsAdministrator_LaterAccountsNeedApproval_AndDisableRevokesSessions()
    {
        await ResetAccountsAsync();
        await using var app = await AppAsync();
        using var anonymous = app.GetTestClient();

        var admin = await RegisterAsync(anonymous, "Admin.One", AdminPassword);
        Assert.Equal(HttpStatusCode.Created, admin.Status);
        Assert.Equal(OperatorStatus.Active, admin.Account!.Status);
        Assert.True(admin.Account.IsAdministrator);

        var staff = await RegisterAsync(anonymous, "staff-01", StaffPassword);
        Assert.Equal(HttpStatusCode.Created, staff.Status);
        Assert.Equal(OperatorStatus.Pending, staff.Account!.Status);
        Assert.False(staff.Account.IsAdministrator);
        var duplicate = await RegisterAsync(anonymous, "STAFF-01", StaffPassword);
        Assert.Equal(OperatorErrorCodes.UsernameTaken, duplicate.Error);
        var invalid = await RegisterAsync(anonymous, "x", "short");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);

        Assert.Equal((HttpStatusCode.Forbidden, OperatorErrorCodes.PendingApproval), await LoginErrorAsync(anonymous, "staff-01", StaffPassword));
        Assert.Equal((HttpStatusCode.Unauthorized, OperatorErrorCodes.InvalidCredentials), await LoginErrorAsync(anonymous, "Admin.One", "wrong-password"));
        Assert.Equal((HttpStatusCode.Unauthorized, OperatorErrorCodes.InvalidCredentials), await LoginErrorAsync(anonymous, "nobody", AdminPassword));

        using var adminClient = await LoggedInClientAsync(app, "admin.one", AdminPassword);
        var me = await adminClient.GetFromJsonAsync<ApiEnvelope<OperatorAccount>>("/api/v1/operators/me", Json);
        Assert.Equal("Admin.One", me!.Result!.Username);
        var listed = await adminClient.GetFromJsonAsync<ApiEnvelope<OperatorAccount[]>>("/api/v1/operators", Json);
        Assert.Equal(["Admin.One", "staff-01"], listed!.Result!.Select(item => item.Username));

        // 请求体中的操作者必须与登录账号一致。
        using (var forged = await adminClient.PutAsJsonAsync("/api/v1/operators/status",
                   new { username = "staff-01", status = "Active", operatorId = "someone-else" }, Json))
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        var approved = await SetStatusAsync(adminClient, "staff-01", OperatorStatus.Active);
        Assert.Equal(OperatorStatus.Active, approved.Account!.Status);
        Assert.Equal("Admin.One", approved.Account.ApprovedBy);

        using var staffClient = await LoggedInClientAsync(app, "staff-01", StaffPassword);
        using (var notAdmin = await staffClient.GetAsync("/api/v1/operators"))
            Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        Assert.Equal(OperatorErrorCodes.NotAdministrator, (await SetStatusAsync(staffClient, "Admin.One", OperatorStatus.Disabled)).Error);

        Assert.Equal(OperatorErrorCodes.LastAdministrator, (await SetStatusAsync(adminClient, "Admin.One", OperatorStatus.Disabled)).Error);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetStatusAsync(adminClient, "staff-01", OperatorStatus.Pending)).Status);

        Assert.Equal(OperatorStatus.Disabled, (await SetStatusAsync(adminClient, "staff-01", OperatorStatus.Disabled)).Account!.Status);
        using (var revoked = await staffClient.GetAsync("/api/v1/operators/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal((HttpStatusCode.Forbidden, OperatorErrorCodes.Disabled), await LoginErrorAsync(anonymous, "staff-01", StaffPassword));

        Assert.Equal(OperatorStatus.Active, (await SetStatusAsync(adminClient, "staff-01", OperatorStatus.Active)).Account!.Status);
        using (var again = await LoggedInClientAsync(app, "staff-01", StaffPassword))
            Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/operators/me")).StatusCode);

        using (var logout = await adminClient.PostAsync("/api/v1/operators/logout", null))
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using (var afterLogout = await adminClient.GetAsync("/api/v1/operators/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);

        // 数据库只保存口令哈希与会话摘要，不保存口令或会话令牌原文。
        await using var stored = fixture.DataSource.CreateCommand("""
            SELECT string_agg(password_hash, '|') FROM exam.operator_accounts;
            """);
        var hashes = (string)(await stored.ExecuteScalarAsync())!;
        Assert.DoesNotContain(AdminPassword, hashes);
        Assert.DoesNotContain(StaffPassword, hashes);
        Assert.StartsWith("pbkdf2-sha256.", hashes);
        var token = staffClient.DefaultRequestHeaders.Authorization!.Parameter!;
        await using var sessions = fixture.DataSource.CreateCommand("SELECT count(*) FROM exam.operator_sessions WHERE token_digest = $1;");
        sessions.Parameters.AddWithValue(token);
        Assert.Equal(0L, await sessions.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConcurrentFirstRegistrationsCreateExactlyOneAdministrator()
    {
        await ResetAccountsAsync();
        await using var app = await AppAsync();
        using var client = app.GetTestClient();
        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(index =>
            RegisterAsync(client, $"racer-{index}", AdminPassword)));
        Assert.All(results, result => Assert.Equal(HttpStatusCode.Created, result.Status));
        Assert.Single(results, result => result.Account!.IsAdministrator && result.Account.Status == OperatorStatus.Active);
        Assert.Equal(5, results.Count(result => result.Account!.Status == OperatorStatus.Pending));
    }

    [Fact]
    public async Task CliRegistersLogsInSavesEncryptedSessionAndLogsOut()
    {
        if (!OperatingSystem.IsWindows()) return; // CLI 会话使用 Windows 当前用户加密保存。
        await ResetAccountsAsync();
        await using var app = await AppAsync(realHttp: true);
        var server = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var sessions = Directory.CreateTempSubdirectory("exam-cli-session-").FullName;
        try
        {
            var registered = await RunCliAsync(server, sessions, ["register", "--username", "cli-admin", "--name", "合成管理员"],
                AdminPassword + "\n" + AdminPassword + "\n");
            Assert.Equal(0, registered.Exit);
            Assert.Contains("管理员", registered.Output);

            Assert.Equal(3, (await RunCliAsync(server, sessions, ["whoami"])).Exit);
            Assert.Equal(3, (await RunCliAsync(server, sessions, ["login", "--username", "cli-admin"], "wrong-password\n")).Exit);
            var login = await RunCliAsync(server, sessions, ["login", "--username", "cli-admin"], AdminPassword + "\n");
            Assert.Equal(0, login.Exit);
            var saved = Assert.Single(Directory.GetFiles(sessions));
            var bytes = await File.ReadAllTextAsync(saved);
            Assert.DoesNotContain("cli-admin", bytes);

            var whoami = await RunCliAsync(server, sessions, ["whoami"]);
            Assert.Equal(0, whoami.Exit);
            Assert.Contains("cli-admin", whoami.Output);

            Assert.Equal(0, (await RunCliAsync(server, sessions, ["register", "--username", "cli-staff", "--name", "合成考务人员"],
                StaffPassword + "\n" + StaffPassword + "\n")).Exit);
            var list = await RunCliAsync(server, sessions, ["operators", "list"]);
            Assert.Equal(0, list.Exit);
            Assert.Contains("cli-staff", list.Output);
            Assert.Equal(0, (await RunCliAsync(server, sessions, ["operators", "approve", "cli-staff"])).Exit);
            Assert.Equal(0, (await RunCliAsync(server, sessions, ["logout"])).Exit);
            Assert.Empty(Directory.GetFiles(sessions));
            Assert.Equal(3, (await RunCliAsync(server, sessions, ["whoami"])).Exit);
            Assert.Equal(0, (await RunCliAsync(server, sessions, ["login", "--username", "cli-staff"], StaffPassword + "\n")).Exit);
        }
        finally { Directory.Delete(sessions, recursive: true); }
    }

    private async Task ResetAccountsAsync()
    {
        await using var command = fixture.DataSource.CreateCommand(
            "DELETE FROM exam.operator_sessions; DELETE FROM exam.operator_accounts;");
        await command.ExecuteNonQueryAsync();
    }

    private async Task<WebApplication> AppAsync(bool realHttp = false)
    {
        var builder = WebApplication.CreateBuilder();
        if (realHttp) builder.WebHost.UseUrls("http://127.0.0.1:0"); else builder.WebHost.UseTestServer();
        builder.Configuration["ExamServer:ServerId"] = "synthetic-account-server";
        builder.Configuration["ExamServer:NodeId"] = "node-a";
        builder.Configuration["ExamServer:ServerEpoch"] = "1";
        builder.Services.ConfigureHttpJsonOptions(options =>
        { foreach (var converter in JsonDefaults.Create().Converters) options.SerializerOptions.Converters.Add(converter); });
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddPostgresOperatorAccounts();
        builder.Services.AddSingleton(new ServerNodeEnvelope("synthetic-account-server", "node-a", 1));
        builder.Services.AddSingleton<IDeviceCredentialValidator, RejectingDeviceValidator>();
        builder.Services.AddSingleton(new ActiveNodeGuard(fixture.DataSource,
            new() { ServerId = "synthetic-account-server", NodeId = "node-a", ServerEpoch = 1 }, true));
        var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.MapGroup("/api/v1").MapOperatorAccountApi();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, OperatorAccount? Account, string? Error)> RegisterAsync(
        HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/operators/register",
            new RegisterOperatorRequest(username, "合成考务人员", password), Json);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<RegisterOperatorResult>>(Json);
        return (response.StatusCode, envelope?.Result?.Account, envelope?.Error?.Code);
    }

    private static async Task<(HttpStatusCode, string?)> LoginErrorAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/operators/login", new LoginOperatorRequest(username, password), Json);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<OperatorSession>>(Json);
        Assert.Null(envelope!.Result);
        return (response.StatusCode, envelope.Error?.Code);
    }

    private static async Task<HttpClient> LoggedInClientAsync(WebApplication app, string username, string password)
    {
        var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/v1/operators/login", new LoginOperatorRequest(username, password), Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<ApiEnvelope<OperatorSession>>(Json))!.Result!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, OperatorAccount? Account, string? Error)> SetStatusAsync(
        HttpClient client, string username, OperatorStatus status)
    {
        using var response = await client.PutAsJsonAsync("/api/v1/operators/status", new SetOperatorStatusRequest(username, status), Json);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<OperatorAccount>>(Json);
        return (response.StatusCode, envelope?.Result, envelope?.Error?.Code);
    }

    private static async Task<(int Exit, string Output)> RunCliAsync(string server, string sessions, string[] args, string? input = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Huaizhou.ExamSystem.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var host = Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "Exam.Cli", "bin", "Release", "net10.0", "Exam.Cli.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--server");
        start.ArgumentList.Add(server);
        start.Environment["EXAM_ALLOW_INSECURE_LOOPBACK"] = "true";
        start.Environment.Remove("EXAM_OPERATOR_TOKEN");
        start.Environment["EXAM_CLI_SESSION_DIRECTORY"] = sessions;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var errors = await error;
        Assert.DoesNotContain(AdminPassword, errors);
        Assert.DoesNotContain(StaffPassword, errors);
        return (process.ExitCode, await output + errors);
    }

    private sealed class RejectingDeviceValidator : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(string deviceId, string credential, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
        public ValueTask<bool> IsActiveDeviceAsync(string deviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }
}
