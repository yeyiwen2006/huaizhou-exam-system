using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Exam.IntegrationTests.Cli;

/// <summary>Real CLI processes against a controlled loopback HTTP host, synthetic input only.</summary>
public sealed class CliBoundaryTests
{
    private static string Repository()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Huaizhou.ExamSystem.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    private static async Task<(int Exit, string Out, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(Repository(), "src/Exam.Cli/bin/Release/net10.0/Exam.Cli.dll"));
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("EXAM_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
        start.Environment["EXAM_ALLOW_INSECURE_LOOPBACK"] = "true";
        start.Environment["EXAM_OPERATOR_TOKEN"] = "synthetic-sensitive-sentinel";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output, await error);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("version")]
    [InlineData("error-codes")]
    public async Task PublicCommandsStaySuccessful(string command)
    {
        var result = await RunAsync(command);
        Assert.Equal(0,result.Exit); Assert.NotEmpty(result.Out); Assert.Empty(result.Error);
        if (command.Contains("help")) Assert.Contains("grading status",result.Out);
    }

    [Theory]
    [InlineData("health", "--server", "https://localhost", "--server", "https://localhost")]
    [InlineData("health", "--server")]
    [InlineData("health", "--server", "https://synthetic-sensitive-sentinel\u0040localhost")]
    [InlineData("health", "--server", "https://localhost?synthetic-sensitive-sentinel")]
    [InlineData("health", "--server", "http://example.invalid")]
    [InlineData("api", "POST", "/test", "--server", "https://localhost", "--input", "missing-synthetic-sensitive-sentinel.json")]
    [InlineData("synthetic-sensitive-sentinel")]
    public async Task InvalidInputIsControlledAndNeverEchoed(params string[] arguments)
    {
        var result = await RunAsync(["--error-format","json",..arguments]);
        Assert.Equal(2,result.Exit); Assert.Empty(result.Out);
        Assert.DoesNotContain("synthetic-sensitive-sentinel",result.Error);
        using var error = JsonDocument.Parse(result.Error);
        Assert.Equal(2,error.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("INVALID_INPUT",error.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(200,0)] [InlineData(400,2)] [InlineData(401,3)] [InlineData(403,3)]
    [InlineData(404,4)] [InlineData(409,4)] [InlineData(500,6)] [InlineData(503,6)] [InlineData(302,4)]
    public async Task RealHttpResponsesMapToExitAndControlledStreams(int status,int exit)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/test",async context =>
        {
            context.Response.StatusCode=status;
            if(status==302) context.Response.Headers.Location="http://127.0.0.1:1/credential-target";
            await context.Response.WriteAsync("{\"result\":\"synthetic-sensitive-sentinel\"}");
        });
        await app.StartAsync();
        var result = await RunAsync("api","GET","/test","--server",app.Urls.Single(),"--error-format","json");
        Assert.Equal(exit,result.Exit);
        if(exit==0) { Assert.Contains("result",result.Out); Assert.Empty(result.Error); }
        else { Assert.Empty(result.Out); Assert.DoesNotContain("synthetic-sensitive-sentinel",result.Error); JsonDocument.Parse(result.Error).Dispose(); }
    }

    [Fact]
    public async Task DeadlineIsNetworkFailureAndMalformedJsonNeverSent()
    {
        var calls=0;
        var builder=WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();
        app.MapMethods("/slow",["GET","POST"],async context => { Interlocked.Increment(ref calls); await Task.Delay(3000,context.RequestAborted); });
        await app.StartAsync();
        var result=await RunAsync("api","GET","/slow","--server",app.Urls.Single(),"--timeout-seconds","1","--error-format","json");
        Assert.Equal(5,result.Exit); Assert.Contains("NETWORK_UNAVAILABLE",result.Error);
        var path=Path.Combine(Path.GetTempPath(),"synthetic-input-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            await File.WriteAllTextAsync(path,"{synthetic-sensitive-sentinel");
            result=await RunAsync("api","POST","/slow","--server",app.Urls.Single(),"--input",path,"--error-format","json");
            Assert.Equal(2,result.Exit); Assert.Equal(1,calls); Assert.DoesNotContain("synthetic-sensitive-sentinel",result.Error);
        }
        finally { File.Delete(path); }
    }
}
