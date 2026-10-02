using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Exam.Admin.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.AdminUnitTests.ServiceManagement;

/// <summary>All identifiers and session values are synthetic protocol fixtures.</summary>
public sealed class ManagementTransportTests
{
    private static readonly ManagementTarget Target = new("https://localhost:9443/", "synthetic-server", "synthetic-node");
    [Theory]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("wrong-server")]
    [InlineData("unverified-epoch")]
    [InlineData("verified-null-epoch")]
    [InlineData("html")]
    [InlineData("wrong-actor")]
    public async Task LoginRejectsUnsafeOrInvalidResponsesBeforeCreatingSession(string scenario)
    {
        var calls = 0;
        using var connection = new ManagementConnection(Target, () => new Handler(request =>
        {
            calls++;
            Assert.Equal("/management/v1/login", request.RequestUri!.AbsolutePath);
            var value = new { token = "synthetic-session", username = scenario == "wrong-actor" ? "different" : "synthetic-operator", isAdministrator = true };
            var response = Reply(value, server: scenario == "wrong-server" ? "different" : Target.ServerId,
                epoch: scenario == "unverified-epoch" ? 9 : null, verified: scenario == "verified-null-epoch");
            if (scenario == "redirect") { response.StatusCode = HttpStatusCode.TemporaryRedirect; response.Headers.Location = new("https://exam.example.test/"); }
            if (scenario == "html") response.Content = new StringContent("<html>synthetic</html>");
            if (scenario == "oversize") response.Content = new StringContent(new string('x', 65537));
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.LoginAsync("synthetic-operator", "synthetic-password", default));
        Assert.Null(connection.Actor); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task SessionTimeoutIncludesBodyReadingAndNeverStoresCredentials()
    {
        using var connection = new ManagementConnection(Target, () => new Handler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new SlowBody() })));
        using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.LoginAsync("synthetic-operator", "synthetic-password", token.Token));
        Assert.Null(connection.Actor);
    }
    [Fact]
    public async Task FailedLogoutClearsLocalSessionWithoutClaimingRemoteRevocation()
    {
        using var connection = new ManagementConnection(Target, () => new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("login", StringComparison.Ordinal) ? Task.FromResult(Login()) : throw new HttpRequestException("synthetic connection lost")));
        await connection.LoginAsync("synthetic-operator", "synthetic-password", default);
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.LogoutAsync(default)); Assert.Null(connection.Actor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connection.StatusAsync(default));
    }
    [Fact]
    public async Task ExpiredReadSessionClearsMemoryAndRetainsServerErrorAsData()
    {
        using var connection = new ManagementConnection(Target, () => new Handler(request =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("login", StringComparison.Ordinal) ? Login() :
                Reply<ManagementStatus>(null!, status: HttpStatusCode.Unauthorized, error: new("UNAUTHORIZED", "synthetic-private-detail")))));
        await connection.LoginAsync("synthetic-operator", "synthetic-password", default);
        var status = await connection.StatusAsync(default); Assert.True(status.IsUnauthorized); Assert.Null(connection.Actor);
    }
    [Fact]
    public async Task CompletedBackupWithoutAllIntegrityFlagsIsRejectedBySharedClient()
    {
        const string id = "synthetic-backup-request";
        var body = JsonSerializer.Serialize(new BackupRequest(id, "C:\\synthetic-backup", "synthetic-operator"), JsonDefaults.Create());
        using var connection = new ManagementConnection(Target, () => new Handler(request =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("login", StringComparison.Ordinal) ? Login() : Reply(new ManagementOperation(
                id, Target.ServerId, Target.NodeId, "synthetic-operator", ManagementOperationKind.Backup, PendingManagementRequest.Hash(body),
                ManagementOperationPhase.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                Backup: new("synthetic-backup", "C:\\synthetic-backup", DateTimeOffset.UtcNow, true, false, true))))));
        await connection.LoginAsync("synthetic-operator", "synthetic-password", default);
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.ExecuteAsync(ManagementOperationKind.Backup, body, default));
    }
    [Fact]
    public async Task ReadDeadlineDoesNotInheritLongWriteTimeout()
    {
        using var connection = new ManagementConnection(Target, () => new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("login", StringComparison.Ordinal)) return Login();
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            return Reply(new ManagementStatus(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown, null, []));
        }), readTimeout: TimeSpan.FromMilliseconds(30));
        await connection.LoginAsync("synthetic-operator", "synthetic-password", default);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StatusAsync(default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }
    private static HttpResponseMessage Login() => Reply(new { token = "synthetic-session", username = "synthetic-operator", isAdministrator = true });
    private static HttpResponseMessage Reply<T>(T result, string? server = null, long? epoch = null, bool verified = false,
        HttpStatusCode status = HttpStatusCode.OK, ApiError? error = null) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(new ManagementEnvelope<T>(server ?? Target.ServerId, Target.NodeId,
        epoch, verified, null, "synthetic-correlation", result, error), JsonDefaults.Create()), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : this((request, token) => send(request).WaitAsync(token)) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class SlowBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(System.IO.Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<System.IO.Stream> CreateContentReadStreamAsync() => Task.FromResult<System.IO.Stream>(new SlowStream());
    }
    private sealed class SlowStream : System.IO.MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
    }
}
