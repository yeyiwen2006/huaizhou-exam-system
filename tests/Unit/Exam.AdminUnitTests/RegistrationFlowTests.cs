using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;

namespace Exam.AdminUnitTests;

public sealed class RegistrationFlowTests
{
    [Fact]
    public async Task PendingAccount_ShowsSuccessUntilAcknowledged_ThenKeepsLoginNotice()
    {
        using var handler = new RegistrationHandler(OperatorStatus.Pending, false);
        using var api = new AdminApiClient(handler);
        var viewModel = ReadyToRegister(api);
        var clearCount = 0;
        viewModel.PasswordsCleared += () => clearCount++;

        await SubmitAsync(viewModel);

        Assert.True(viewModel.ShowRegistrationSuccess);
        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsRegistering);
        Assert.Equal("注册成功，请等待管理员批准后再登录。", viewModel.Status);
        Assert.Equal(string.Empty, viewModel.Password);
        Assert.Equal(string.Empty, viewModel.ConfirmPassword);
        Assert.Equal(1, clearCount);

        viewModel.AcknowledgeRegistrationCommand.Execute(null);

        Assert.False(viewModel.ShowRegistrationSuccess);
        Assert.False(viewModel.IsConnected);
        Assert.Equal("staff-demo", viewModel.Username);
        Assert.Equal("注册成功，请等待管理员批准后再登录。", viewModel.Status);
        Assert.Same(viewModel.ConnectCommand, viewModel.PrimaryActionCommand);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FirstAdministrator_ReturnsToLoginWithoutApprovalSuccessPage()
    {
        using var handler = new RegistrationHandler(OperatorStatus.Active, true);
        using var api = new AdminApiClient(handler);
        var viewModel = ReadyToRegister(api);

        await SubmitAsync(viewModel);

        Assert.False(viewModel.ShowRegistrationSuccess);
        Assert.False(viewModel.IsRegistering);
        Assert.False(viewModel.IsConnected);
        Assert.Contains("已设为管理员，请输入密码登录", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedRegistration_StaysInRegistrationWithoutSuccessPage()
    {
        using var handler = new RegistrationHandler(OperatorStatus.Pending, false, reject: true);
        using var api = new AdminApiClient(handler);
        var viewModel = ReadyToRegister(api);

        await SubmitAsync(viewModel);

        Assert.False(viewModel.ShowRegistrationSuccess);
        Assert.True(viewModel.IsRegistering);
        Assert.False(viewModel.IsConnected);
        Assert.StartsWith("注册失败：", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.Password);
    }

    private static ConnectionViewModel ReadyToRegister(AdminApiClient api) => new(api)
    {
        Username = "staff-demo",
        DisplayName = "合成考务账号",
        Password = "synthetic-password",
        ConfirmPassword = "synthetic-password",
        IsRegistering = true
    };

    private static async Task SubmitAsync(ConnectionViewModel viewModel)
    {
        viewModel.RegisterCommand.Execute(null);
        for (var i = 0; i < 200 && !viewModel.RegisterCommand.CanExecute(null); i++) await Task.Delay(10);
        Assert.True(viewModel.RegisterCommand.CanExecute(null), "注册命令没有在预期时间内结束。");
    }

    private sealed class RegistrationHandler(OperatorStatus status, bool isAdministrator, bool reject = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("/api/v1/operators/register", request.RequestUri!.AbsolutePath);
            var account = new OperatorAccount("staff-demo", "合成考务账号", status, isAdministrator,
                new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(8)), null);
            var envelope = new ApiEnvelope<RegisterOperatorResult>("synthetic-server", "synthetic-node", 1,
                "synthetic-request", reject ? null : new RegisterOperatorResult(account),
                reject ? new ApiError(OperatorErrorCodes.UsernameTaken, "该用户名已被注册。") : null);
            return Task.FromResult(new HttpResponseMessage(reject ? HttpStatusCode.Conflict : HttpStatusCode.Created)
            {
                Content = JsonContent.Create(envelope, options: JsonDefaults.Create())
            });
        }
    }
}
