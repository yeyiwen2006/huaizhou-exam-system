using System.Reflection;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;

internal static partial class ExamCli
{
    private static readonly AsyncLocal<ControlledErrors?> ActiveErrors = new();

    // The boundary owns errors only. Successful business output stays byte-compatible.
    public static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var original = Console.Error;
        using var errors = new ControlledErrors(original);
        var previousErrors = ActiveErrors.Value;
        ActiveErrors.Value = errors;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var json = Enumerable.Range(0, Math.Max(0, arguments.Length - 1))
            .Any(index => arguments[index] == "--error-format" && arguments[index + 1] == "json");
        var exit = 6;
        var localImport = false;
        string? importAction = null;
        var gradingPublication = false;
        Console.SetError(errors);
        try
        {
            var args = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < arguments.Length; i++)
            {
                var value = arguments[i];
                if (value == "--help") { args.Add(value); continue; }
                // paper import confirmation is a switch, not a key/value option. Keep it command-specific,
                // so accepting it does not loosen unrelated command validation.
                if (value == "--confirm" && args.Count >= 3 && args[0] is ("import" or "grading") && args[1] == "publish")
                {
                    if (!seen.Add(value)) throw new ArgumentException("确认选项重复。");
                    args.Add(value); continue;
                }
                if (!value.StartsWith("--", StringComparison.Ordinal)) { args.Add(value); continue; }
                if (!seen.Add(value) || i + 1 >= arguments.Length ||
                    arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("选项重复或缺少值。");
                if (value == "--error-format")
                {
                    var format = arguments[++i];
                    json = format == "json";
                    if (format is not ("json" or "text")) throw new ArgumentException("错误格式无效。");
                }
                else if (value == "--timeout-seconds")
                {
                    if (!int.TryParse(arguments[++i], out var seconds) || seconds is < 1 or > 3900)
                        throw new ArgumentException("超时无效。");
                    deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                }
                else { args.Add(value); args.Add(arguments[++i]); }
            }
            // Validate shared address semantics even for commands with their own parser.
            importAction = args.Count > 1 && args[0] == "import" ? args[1] : null;
            localImport = importAction == "verify";
            gradingPublication = args.Count > 1 && args[0] == "grading" && args[1] == "publish";
            var serverIndex = args.IndexOf("--server");
            var server = serverIndex >= 0 ? args[serverIndex + 1] : Environment.GetEnvironmentVariable(ServerEnvironment);
            if (args.Count > 0 && args[0] is not ("import" or "management" or "version" or "artifact-info" or "help" or "--help" or "-h" or "error-codes") &&
                server is not null && !TryResolveServer(server, out _)) throw new ArgumentException("服务地址无效。");
            errors.JsonFormat = json;
            exit = await DispatchAsync(args.ToArray(), deadline.Token);
            if (deadline.IsCancellationRequested) exit = cancellationToken.IsCancellationRequested ? 130 : 5;
        }
        catch (OperationCanceledException) { exit = cancellationToken.IsCancellationRequested ? 130 : 5; }
        catch (ArgumentException) { exit = 2; }
        catch (Exception) { exit = 6; }
        finally { Console.SetError(original); ActiveErrors.Value = previousErrors; }
        if (exit == 0)
        {
            // Only explicitly registered, locally formatted success diagnostics are forwarded.
            // Never flush the captured raw error stream, including server response bodies.
            original.Write(errors.SuccessLines.ToString());
            return 0;
        }
        var (code, message) = exit switch
        {
            2 => ("INVALID_INPUT", "命令、选项、配置或输入文件无效；运行 help 查看用法。"),
            3 => ("ACCESS_DENIED", "身份未认证或没有操作权限。"),
            4 => ("BUSINESS_REJECTED", "对象不存在、状态冲突或业务条件未满足。"),
            5 => ("NETWORK_UNAVAILABLE", "连接失败或请求超时。"),
            130 => ("CANCELLED", "操作已取消。"),
            _ => ("CLI_ERROR", "执行失败；请检查服务状态及受保护的本机诊断。")
        };
        if (localImport && exit is 3 or 4)
        {
            // Preserve the local importer contract, exposing only declared public issue IDs.
            var declared = exit == 3
                ? typeof(ManifestRuleIds).GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!)
                : Enum.GetNames<OptionRegionIssueCode>().Where(value => value != nameof(OptionRegionIssueCode.None));
            var diagnostic = errors.Lines.ToString();
            var publicIssues = declared.Where(value => diagnostic.Contains(value, StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal);
            code = exit == 3 ? "IMPORT_VALIDATION_FAILED" : "IMPORT_COMPILATION_FAILED";
            message = "导入核对未通过。 " + string.Join(" ", publicIssues);
        }
        if ((importAction == "publish" || gradingPublication) && exit is 4 or 5 or 6 or 130)
            message = "发布结果未确认；请保留原预览目录，核对考试状态后使用同一目录重试，不要重新生成另一份发布请求。";
        else if (importAction == "preview" && exit == 4)
            message = "材料、编译结果或考试设置校验未通过；未发布，请核对试题包与已保存的考试设置。";
        else if (importAction == "materials" && exit == 4)
            message = "取回源材料未完成；请核对考试名称与当前已发布版本，未写出任何目录。";
        // Only contract constants may cross the error boundary; never echo a response body.
        var contractCodes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);
        try
        {
            using var body = JsonDocument.Parse(errors.Lines.ToString());
            if (body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var field) && field.ValueKind == JsonValueKind.String &&
                contractCodes.Contains(field.GetString(), StringComparer.Ordinal)) code = field.GetString()!;
        }
        catch (JsonException) { }
        original.WriteLine(json
            ? JsonSerializer.Serialize(new { schemaVersion = 1, code, message, exitCode = exit }, JsonDefaults.Create())
            : $"{code}: {message}");
        return exit;
    }

    private static void WriteSuccessDiagnostic(string message)
    {
        if (ActiveErrors.Value is { } errors) errors.RecordSuccess(message);
        else Console.Error.WriteLine(message);
    }

    // 只供命令自身生成的进度文案使用；不接受服务器正文或异常消息。
    private static void WriteLiveDiagnostic(string message)
    {
        if (ActiveErrors.Value is { } errors) errors.RecordProgress(message);
        else Console.Error.WriteLine(message);
    }

    private sealed class ControlledErrors(TextWriter terminal) : TextWriter
    {
        public bool JsonFormat { get; set; }
        public void RecordProgress(string value)
        {
            if (JsonFormat) RecordSuccess(value);
            else { terminal.WriteLine(value); terminal.Flush(); }
        }
        public StringBuilder Lines { get; } = new();
        public StringBuilder SuccessLines { get; } = new();
        public void RecordSuccess(string value)
        {
            if (SuccessLines.Length + value.Length < 65536) SuccessLines.AppendLine(value);
        }
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            if (Lines.Length + (value?.Length ?? 0) < 65536) Lines.AppendLine(value);
        }
        public override void WriteLine() => terminal.WriteLine();
        // Interactive labels are fixed strings owned by the account commands.
        public override void Write(string? value) => terminal.Write(value);
        public override void Write(char value) => terminal.Write(value);
    }
}
