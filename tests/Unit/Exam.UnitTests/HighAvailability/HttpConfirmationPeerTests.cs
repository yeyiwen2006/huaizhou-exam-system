using System.Net;
using System.Net.Http.Json;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.HighAvailability;

namespace Exam.UnitTests.HighAvailability;

public sealed class HttpConfirmationPeerTests
{
    private const string Secret = "synthetic-confirmation-peer-token-only";
    private static DurableConfirmation Record() => new(Guid.NewGuid().ToString("N"), "server", "primary", "standby",
        1, 1, ProtectionMode.SynchronousDualNode, "12345", "exam", 1, "0/100", new string('A', 64));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineIncludesAStalledBodyAfterSuccessfulHeaders(bool write)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledBody()) }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var peer = new HttpConfirmationPeer(http, new Uri("https://standby.invalid"), "server", "standby", () => Secret);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (write) await peer.ConfirmAsync(Record(), caller.Token);
            else await peer.ReadAsync(caller.Token);
        });
        Assert.False(caller.IsCancellationRequested, "The peer deadline, not the outer caller deadline, must end a stalled body.");
    }

    [Fact]
    public async Task CallerCancellationStillAbortsTheBodyBeforePeerDeadline()
    {
        using var caller = new CancellationTokenSource();
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledBody(() => caller.Cancel())) }));
        using var http = new HttpClient(handler);
        var peer = new HttpConfirmationPeer(http, new Uri("https://standby.invalid"), "server", "standby", () => Secret);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.ReadAsync(caller.Token));
        Assert.True(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task CompleteResponseRetainsTheExactAuthenticatedBoundary()
    {
        var record = Record();
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://standby.invalid/replication/v1/confirmation", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Secret, request.Headers.Authorization.Parameter);
            Assert.Equal("server", Assert.Single(request.Headers.GetValues("X-Exam-Server-Id")));
            Assert.Equal("standby", Assert.Single(request.Headers.GetValues("X-Exam-Node-Id")));
            Assert.Equal(record, await request.Content!.ReadFromJsonAsync<DurableConfirmation>(JsonDefaults.Create(), token));
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(record, options: JsonDefaults.Create()) };
        });
        using var http = new HttpClient(handler);
        var peer = new HttpConfirmationPeer(http, new Uri("https://standby.invalid"), "server", "standby", () => Secret);
        Assert.Equal(record, await peer.ConfirmAsync(record, default));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class StalledBody(Action? onRead = null) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
