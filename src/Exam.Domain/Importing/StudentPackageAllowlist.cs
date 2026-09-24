using System.Text.Json;

namespace Exam.Domain.Importing;

/// <summary>
/// 学生端包的允许清单规则。依据 requirements.md「逐题导入、原版显示与加密」：
/// 「学生端包按允许文件清单生成，仅含试题 PDF、题目所需图片、必要显示资源和无答案元数据」、
/// 「答案、解析及其专用资源留在服务器，不直接分发原始目录」。
///
/// 两条基本原则：
/// 1. **默认拒绝**：组包只复制清单允许的路径，绝不「先全量复制再删除」——后者一旦漏删就会泄题。
/// 2. **不能仅按扩展名筛选**：除路径规则外还要看内容（例如 JSON 里出现 correctOptionIds）。
///    路径关键词检查（答案/解析/answer/solution）只是补充手段，单独依赖会漏掉改了名的答案文件。
/// </summary>
public static class StudentPackageAllowlist
{
    /// <summary>服务器专用目录：其下任何内容都不进学生端包。</summary>
    public const string ServerOnlyDirectory = "server-only";

    private static readonly string[] AnswerKeywords =
    [
        "答案", "解析", "answer", "solution"
    ];

    /// <summary>无答案元数据里不允许出现的 JSON 字段名（小写比较）。</summary>
    private static readonly string[] AnswerBearingJsonKeys =
    [
        "correctoptionids", "correctanswer", "answers", "solution", "solutions"
    ];

    /// <summary>服务器专用目录及其子路径。</summary>
    public static bool IsServerOnly(string? relativePath, out string? reason)
    {
        reason = null;
        var normalized = Normalize(relativePath);
        if (normalized is null)
        {
            reason = "路径为空。";
            return false;
        }

        var firstSegment = normalized.Split('/')[0];
        if (!string.Equals(firstSegment, ServerOnlyDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        reason = $"位于服务器专用目录 {ServerOnlyDirectory}/，不得进入学生端包。";
        return true;
    }

    /// <summary>路径或文件名里带有答案、解析字样。补充手段，不能单独作为放行依据。</summary>
    public static bool LooksLikeAnswerResource(string? relativePath, out string? reason)
    {
        reason = null;
        var normalized = Normalize(relativePath);
        if (normalized is null)
        {
            return false;
        }

        foreach (var keyword in AnswerKeywords)
        {
            if (normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"路径或文件名包含「{keyword}」，按答案或解析资源处理，不得进入学生端包。";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 内容检查：文本里出现答案相关字段。这是「不能仅按扩展名筛选」的落点——
    /// 一个改了名的 .json 或 .txt 只要带有 correctOptionIds 就必须被挡下。
    ///
    /// 判定要看**字段名**而不是关键词出现：可解析的 JSON 按属性名判断；
    /// 其它文本只认带引号的字段名。否则样式文件里名为 \answers 或 \solution 的宏会被误伤，
    /// 把本该进包的共享样式悄悄排除掉。
    /// </summary>
    public static bool ContainsAnswerBearingJson(string? content, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        if (TryReadPropertyNames(content, out var propertyNames))
        {
            foreach (var name in propertyNames)
            {
                if (IsAnswerBearingKey(name))
                {
                    reason = $"内容里出现答案相关字段「{name}」，不得进入学生端包。";
                    return true;
                }
            }

            return false;
        }

        var lowered = content.ToLowerInvariant();
        foreach (var key in AnswerBearingJsonKeys)
        {
            if (lowered.Contains($"\"{key}\"", StringComparison.Ordinal))
            {
                reason = $"内容里出现答案相关字段「{key}」，不得进入学生端包。";
                return true;
            }
        }

        return false;
    }

    private static bool IsAnswerBearingKey(string propertyName) =>
        AnswerBearingJsonKeys.Contains(propertyName.ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>
    /// 尝试把文本当 JSON 读出所有属性名（含嵌套）。取样被截断或不是 JSON 时返回 false，
    /// 由调用方退回到带引号的字段名检查。
    /// </summary>
    private static bool TryReadPropertyNames(string content, out List<string> propertyNames)
    {
        propertyNames = [];
        var trimmed = content.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            Collect(document.RootElement, propertyNames);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Collect(JsonElement element, List<string> propertyNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    propertyNames.Add(property.Name);
                    Collect(property.Value, propertyNames);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, propertyNames);
                }

                break;
        }
    }

    /// <summary>
    /// 综合判定某个包内相对路径是否可以进入学生端包。content 只在调用方读得到时给出
    /// （目录项、二进制文件传 null）；路径检查始终生效。
    /// </summary>
    public static bool IsAllowed(string? relativePath, string? content, out string? rejectionReason)
    {
        if (IsServerOnly(relativePath, out rejectionReason)
            || LooksLikeAnswerResource(relativePath, out rejectionReason)
            || ContainsAnswerBearingJson(content, out rejectionReason))
        {
            return false;
        }

        if (Normalize(relativePath) is null)
        {
            rejectionReason = "路径为空。";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    private static string? Normalize(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Length == 0 ? null : normalized;
    }
}
