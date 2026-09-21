using System.Reflection;
using System.Text.Json;
using Exam.Contracts.Common;

return args switch
{
    ["version"] => PrintVersion(),
    ["error-codes"] => PrintErrorCodes(),
    [] or ["help"] or ["--help"] or ["-h"] => PrintHelp(),
    _ => UnknownCommand(args)
};

static int PrintVersion()
{
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "unknown";
    Console.WriteLine(version);
    return 0;
}

static int PrintErrorCodes()
{
    var values = typeof(ErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .Order(StringComparer.Ordinal)
        .ToArray();
    Console.WriteLine(JsonSerializer.Serialize(values, JsonDefaults.Create()));
    return 0;
}

static int PrintHelp()
{
    Console.WriteLine("怀籀传承在线考试系统 CLI");
    Console.WriteLine("  version       显示当前程序版本");
    Console.WriteLine("  error-codes   以 JSON 输出公共错误码");
    Console.WriteLine("各业务命令由对应任务负责人通过 Exam.Application 接入；当前骨架不会伪造成功结果。");
    return 0;
}

static int UnknownCommand(IReadOnlyList<string> arguments)
{
    Console.Error.WriteLine($"未知命令：{string.Join(' ', arguments)}");
    return 2;
}
