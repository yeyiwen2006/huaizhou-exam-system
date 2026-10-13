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
    [Theory]
    [InlineData("exam.example.com", "https://exam.example.com/")]
    [InlineData("192.0.2.10", "https://192.0.2.10/")]
    [InlineData("2001:db8::1", "https://[2001:db8::1]/")]
    [InlineData("https://exam.example.com:5001/", "https://exam.example.com:5001/")]
    [InlineData("https://[::1]:5001/", "https://[::1]:5001/")]
    public async Task ConfiguredAddress_UsesHttpsByDefault_AndPreservesExplicitHttpsPorts(
        string configured, string endpoint)
    {
        using var handler = new RegistrationHandler(OperatorStatus.Pending, false);
        using var api = new AdminApiClient(handler);
        var viewModel = ReadyToRegister(api, configured);
        Assert.Equal(configured, viewModel.ServerAddress);

        await SubmitAsync(viewModel);

        Assert.True(viewModel.ShowRegistrationSuccess, viewModel.Status);
        Assert.Equal(new Uri(new Uri(endpoint), "api/v1/operators/register"), handler.LastRequestUri);
    }

    [Fact]
    public async Task ChangingServerAddress_UsesStandardHttpsPort()
    {
        using var handler = new RegistrationHandler(OperatorStatus.Pending, false);
        using var api = new AdminApiClient(handler);
        var viewModel = ReadyToRegister(api, "https://old.example.com:5443/");
        viewModel.ServerAddress = "exam.example.com";

        await SubmitAsync(viewModel);

        Assert.True(viewModel.ShowRegistrationSuccess, viewModel.Status);
        Assert.Equal(new Uri("https://exam.example.com/api/v1/operators/register"), handler.LastRequestUri);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5089/")]
    [InlineData("http://exam.example.com/")]
    [InlineData("exam.example.com:5001")]
    [InlineData("exam.example.com/path")]
    [InlineData("https://exam.example.com:0/")]
    [InlineData("https://exam.example.com:65536/")]
    [InlineData("https://exam.example.com/path")]
    [InlineData("https://user@exam.example.com/")]
    [InlineData("")]
    public async Task InvalidAddress_ShowsClearErrorBeforeConnecting(string address)
    {
        using var handler = new RegistrationHandler(OperatorStatus.Pending, false);
        using var api = new AdminApiClient(handler);
        var viewModel = ReadyToRegister(api);
        viewModel.ServerAddress = address;

        await SubmitAsync(viewModel);

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal("注册失败：请填写服务器 IP、域名或 HTTPS 根地址，例如 exam.example.com；默认使用 HTTPS 443 端口。", viewModel.Status);
    }

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

    private static ConnectionViewModel ReadyToRegister(AdminApiClient api, string? configured = null) => new(api, configured ?? "exam.example.com")
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
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
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
