using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

namespace Exam.UnitTests.Cli;

[CollectionDefinition("CLI public console boundary", DisableParallelization = true)]
public sealed class CliConsoleCollection;

[Collection("CLI public console boundary")]
public sealed class PublicCancellationTests
{
    private static Task<int> RunAsync(string[] args, CancellationToken token) =>
        (Task<int>)typeof(ManagementCli).Assembly.GetType("ExamCli")!.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [args, token])!;

    [Fact]
    public async Task ActiveRequestCancellationReturns130AndControlledJson()
    {
        var originalOut = Console.Out; var originalError = Console.Error;
        var previous = Environment.GetEnvironmentVariable("EXAM_ALLOW_INSECURE_LOOPBACK");
        using var output = new StringWriter(); using var error = new StringWriter();
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var cancellation = new CancellationTokenSource();
        try
        {
            Environment.SetEnvironmentVariable("EXAM_ALLOW_INSECURE_LOOPBACK", "true");
            Console.SetOut(output); Console.SetError(error);
            var pending = RunAsync(["health", "--server", $"http://127.0.0.1:{port}", "--error-format", "json"], cancellation.Token);
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.Equal(130, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(output.ToString());
            using var body = JsonDocument.Parse(error.ToString());
            Assert.Equal("CANCELLED", body.RootElement.GetProperty("code").GetString());
            Assert.Equal(130, body.RootElement.GetProperty("exitCode").GetInt32());
            context.Response.Close();
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); Environment.SetEnvironmentVariable("EXAM_ALLOW_INSECURE_LOOPBACK", previous); }
    }

    [Fact]
    public async Task JsonErrorModeIsHonoredWhenBadArgumentsPrecedeIt()
    {
        var original = Console.Error; using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            Assert.Equal(2, await RunAsync(["health", "--server", "--error-format", "json"], default));
            using var body = JsonDocument.Parse(error.ToString());
            Assert.Equal("INVALID_INPUT", body.RootElement.GetProperty("code").GetString());
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public async Task LocalImportPreservesItsContractWithoutEchoingInputOrUsingServerEnvironment()
    {
        var original = Console.Error; using var error = new StringWriter();
        var server = Environment.GetEnvironmentVariable("EXAM_SERVER_URL");
        var directory = Path.Combine(Path.GetTempPath(), "exam-e01-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "{\"schemaVersion\":1,\"examId\":\"synthetic-private-exam\"}");
        try
        {
            Environment.SetEnvironmentVariable("EXAM_SERVER_URL", "http://invalid.example.test");
            Console.SetError(error);
            Assert.Equal(3, await RunAsync(["--error-format", "json", "import", "verify", directory], default));
            using var body = JsonDocument.Parse(error.ToString());
            Assert.Equal("IMPORT_VALIDATION_FAILED", body.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(directory, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-private-exam", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(original); Environment.SetEnvironmentVariable("EXAM_SERVER_URL", server);
            Directory.Delete(directory, recursive: true);
        }
    }
}
