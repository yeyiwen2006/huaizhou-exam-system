using System.Text.Json.Nodes;

namespace Exam.Infrastructure.Archives;

/// <summary>Rewrites identity fields only. Candidate answers and arbitrary prose are never search/replaced.</summary>
internal sealed class ExamArchiveIdentityMap(string sourceExamId, string targetExamId, string examName)
{
    private readonly Dictionary<string, string> ids = new(StringComparer.Ordinal);
    private static readonly HashSet<string> IdentityFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "participationid", "participantid", "submissionid", "receiptid", "deviceid", "authorizeddeviceid",
        "originaldeviceid", "pendingdeviceid", "selecteddeviceid", "revokeddeviceid", "targetdeviceid",
        "sessionid", "preparationid", "decisionid", "eventid", "requestid", "createrequestid",
        "acknowledgementrequestid", "lookuprequestid", "resultrequestid", "artifactid", "resourceid", "rulesetid"
    };
    private static readonly HashSet<string> ContentFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "answers", "frozenanswers", "questionscores", "subjectscores", "questiondigests", "settings",
        "structure", "subjects", "questions", "lines", "changedquestionids"
    };
    public Dictionary<string, string> MaterialHashes { get; } = new(StringComparer.Ordinal);
    public string Id(string source) => ids.TryGetValue(source, out var result) ? result : ids[source] = "archive-" + Guid.NewGuid().ToString("N");
    public void Bind(string source, string target) => ids[source] = target;

    public void Rewrite(JsonNode? node)
    {
        if (node is JsonArray array) { foreach (var child in array) Rewrite(child); return; }
        if (node is not JsonObject obj) return;
        foreach (var (key, value) in obj.ToArray())
        {
            var normalized = key.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
            if (ContentFields.Contains(normalized)) continue;
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                if (normalized.Equals("examid", StringComparison.OrdinalIgnoreCase))
                {
                    if (text != sourceExamId) throw new InvalidDataException("归档混入其他考试的记录。");
                    obj[key] = targetExamId;
                }
                else if (IdentityFields.Contains(normalized) && text.Length > 0) obj[key] = Id(text);
                else if (normalized.Equals("manifestjson", StringComparison.OrdinalIgnoreCase))
                    obj[key] = PortableExamResources.RebindManifestJson(text, targetExamId, examName);
                else if (normalized.Equals("answerkeyjson", StringComparison.OrdinalIgnoreCase))
                {
                    obj[key] = PortableExamResources.RebindAnswerKeyJson(text, targetExamId);
                }
                else if (normalized.Equals("materialssha256", StringComparison.OrdinalIgnoreCase) && MaterialHashes.TryGetValue(text, out var digest)) obj[key] = digest;
                else if (normalized is "unlockreference" or "protectedunlockreference")
                {
                    const string prefix = "resource-envelope:";
                    if (!text.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("归档资源解锁引用无效。");
                    obj[key] = prefix + Id(text[prefix.Length..]);
                }
                else if (normalized.Equals("encryptedpackageuri", StringComparison.OrdinalIgnoreCase) && text.Length > 0)
                {
                    var resource = Uri.UnescapeDataString(text.Split('?', '#')[0].TrimEnd('/').Split('/')[^1]);
                    obj[key] = "/api/v1/resources/" + Uri.EscapeDataString(Id(resource));
                }
                else if (normalized == "scope" && text == "roster:" + sourceExamId) obj[key] = "roster:" + targetExamId;
            }
            else if (normalized.Equals("participants", StringComparison.OrdinalIgnoreCase) && value is JsonObject participants)
            {
                var rewritten = new JsonObject();
                foreach (var (participantId, participant) in participants)
                {
                    var copy = participant?.DeepClone(); Rewrite(copy); rewritten[Id(participantId)] = copy;
                }
                obj[key] = rewritten;
            }
            else Rewrite(value);
        }
    }
}
