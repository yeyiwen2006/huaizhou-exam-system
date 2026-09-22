using Exam.LoadTests;
using System.Text.Json;

if (args is ["--help"] or ["-h"] or ["help"])
{
    Console.WriteLine("Exam.LoadTests --target <origin> --sessions <synthetic.json> --scenario burst|steady|final|commands|download|mixed --output <new-directory> --confirm-synthetic [--duration-seconds 60] [--timeout-seconds 30] [--allow-loopback-http]");
    Console.WriteLine("仅在隔离服务器运行；验证 TLS，禁止重定向；本机报告不代表双机或实机验收。commands 仅确认收到，不伪称已应用。");
    return 0;
}
try
{
    var options = LoadOptions.Parse(args);
    var manifest = JsonSerializer.Deserialize<LoadManifest>(await File.ReadAllTextAsync(options.SessionsPath), LoadJson.Options)
        ?? throw new ArgumentException("会话文件为空。");
    manifest.Validate(options);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    await using var runner = new LoadRunner(options, manifest);
    return await runner.RunAsync(stop.Token);
}
catch (Exception error) when (error is ArgumentException or IOException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"负载工具未启动或报告写入失败：{error.GetType().Name}。请检查参数、合成会话和输出目录；使用 --help 查看格式。");
    return 2;
}
