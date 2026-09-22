using System.Text.Json;
using System.Text.Json.Serialization;

namespace Exam.LoadTests;

public static class LoadJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record LoadOptions(Uri Target, string SessionsPath, string Scenario, string OutputDirectory,
    int DurationSeconds, int TimeoutSeconds, bool AllowLoopbackHttp)
{
    public static LoadOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--confirm-synthetic" or "--allow-loopback-http")
            {
                if (!flags.Add(key)) throw new ArgumentException("重复选项。");
            }
            else if (key is "--target" or "--sessions" or "--scenario" or "--output" or "--duration-seconds" or "--timeout-seconds")
            {
                if (++i >= args.Length || !values.TryAdd(key, args[i])) throw new ArgumentException("参数缺失或重复。");
            }
            else throw new ArgumentException("未知选项。");
        }
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("缺少必需参数。");
        int Number(string key, int fallback, int maximum) => !values.TryGetValue(key, out var text) ? fallback :
            int.TryParse(text, out var number) && number >= 1 && number <= maximum ? number : throw new ArgumentException("数字越界。");
        if (!flags.Contains("--confirm-synthetic")) throw new ArgumentException("必须确认隔离合成数据。");
        if (!Uri.TryCreate(Required("--target"), UriKind.Absolute, out var target) ||
            target.AbsolutePath != "/" || target.Query.Length != 0 || target.Fragment.Length != 0 || target.UserInfo.Length != 0 ||
            (target.Scheme != "https" && !(target.Scheme == "http" && target.IsLoopback && flags.Contains("--allow-loopback-http"))))
            throw new ArgumentException("需要 HTTPS 原点；HTTP 仅允许显式回环测试。");
        var scenario = Required("--scenario");
        if (scenario is not ("burst" or "steady" or "final" or "commands" or "download" or "mixed")) throw new ArgumentException("未知场景。");
        var output = Path.GetFullPath(Required("--output"));
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("输出目录必须尚不存在。");
        return new(target, Path.GetFullPath(Required("--sessions")), scenario, output,
            Number("--duration-seconds", 60, 86400), Number("--timeout-seconds", 30, 300), flags.Contains("--allow-loopback-http"));
    }
}

public sealed class LoadManifest
{
    public bool Synthetic { get; init; }
    public string ServerId { get; init; } = "";
    public long ServerEpoch { get; init; }
    public int QuestionCount { get; init; } = 120;
    public string QuestionPrefix { get; init; } = "q";
    public string[] OptionIds { get; init; } = ["A", "B", "C", "D"];
    public string[] ExpectedCommandEventIds { get; init; } = [];
    public string? OperatorToken { get; init; }
    public LoadSession[] Sessions { get; init; } = [];

    public void Validate(LoadOptions options)
    {
        if (!Synthetic || string.IsNullOrWhiteSpace(ServerId) || ServerEpoch < 1 || QuestionCount is < 120 or > 10000 ||
            string.IsNullOrWhiteSpace(QuestionPrefix) || OptionIds is not { Length: > 0 } || OptionIds.Any(string.IsNullOrWhiteSpace) ||
            ExpectedCommandEventIds is null || Sessions is not { Length: > 0 and <= 1000 } || Sessions.Any(s => s is null || !s.IsValid()))
            throw new ArgumentException("无效的合成会话清单。");
        if (Sessions.Select(s => s.DeviceId).Distinct().Count() != Sessions.Length ||
            Sessions.Select(s => s.CandidateId).Distinct().Count() != Sessions.Length ||
            Sessions.Select(s => s.ParticipationId).Distinct().Count() != Sessions.Length)
            throw new ArgumentException("会话必须使用独立设备、考生和参试记录。");
        if (options.Scenario is "commands" or "mixed" && string.IsNullOrWhiteSpace(OperatorToken) &&
            (ExpectedCommandEventIds.Length == 0 || ExpectedCommandEventIds.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("指令场景必须声明预期事件。");
        if (options.Scenario is "download" or "mixed" && Sessions.Any(s =>
            string.IsNullOrEmpty(s.ResourcePath) || !s.ResourcePath.StartsWith("/api/v1/resources/", StringComparison.Ordinal) ||
            s.ResourcePath.Contains('\\') || new Uri(options.Target, s.ResourcePath).GetLeftPart(UriPartial.Authority) != options.Target.GetLeftPart(UriPartial.Authority) ||
            s.ExpectedResourceBytes < 1)) throw new ArgumentException("下载须声明本服务器资源路径及准确长度。");
    }
}

public sealed class LoadSession
{
    public string ExamId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public string ParticipationId { get; init; } = "";
    public long EntryVersion { get; init; }
    public string DeviceId { get; init; } = "";
    public long AuthorizationVersion { get; init; }
    public long PaperVersion { get; init; }
    public string Credential { get; init; } = "";
    public long InitialAnswerVersion { get; init; }
    public string? ResourcePath { get; init; }
    public long ExpectedResourceBytes { get; init; }
    public bool IsValid() => new[] { ExamId, CandidateId, ParticipationId, DeviceId, Credential }.All(v => !string.IsNullOrWhiteSpace(v) && !v.Any(char.IsControl)) &&
        EntryVersion > 0 && AuthorizationVersion > 0 && PaperVersion > 0 && InitialAnswerVersion >= 0;
    public string Query => $"participationId={Uri.EscapeDataString(ParticipationId)}&entryVersion={EntryVersion}&deviceId={Uri.EscapeDataString(DeviceId)}&authorizationVersion={AuthorizationVersion}";
}
