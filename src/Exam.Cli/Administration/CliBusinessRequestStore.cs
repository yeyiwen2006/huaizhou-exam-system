using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;

internal sealed record CliBusinessRequest(string Path, JsonElement Body);

/// <summary>Persist resolved mutations before sending; retry the original request, not a changed monitoring snapshot.</summary>
internal sealed class CliBusinessRequestStore : IDisposable
{
    private sealed record SavedRequest(string ServerId, string Operator, string RequestId, string IntentHash, CliBusinessRequest Request);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Huaizhou.Exam.Cli.BusinessRequest.v1");
    private readonly string _server, _operator, _requestId, _path;
    private readonly FileStream _claim;

    public CliBusinessRequestStore(string server, string account, string requestId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("考务业务命令的可靠重试记录需要 Windows 当前用户保护。");
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200) throw new ArgumentException("操作请求标识无效。");
        _server = server; _operator = account; _requestId = requestId;
        var directory = Path.GetFullPath(Path.Combine(CliSessionStore.Directory, "operations"));
        RejectLinks(directory); Directory.CreateDirectory(directory); RejectLinks(directory);
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { server, account, requestId })));
        _path = Path.Combine(directory, key + ".request"); RejectLinks(_path); RejectLinks(_path + ".lock");
        _claim = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task<CliBusinessRequest> GetOrCreateAsync(string intent, Func<Task<CliBusinessRequest>> prepare, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("可靠业务请求保存需要 Windows。");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(intent)));
        if (File.Exists(_path))
        {
            if (new FileInfo(_path).Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("原操作记录损坏，已阻止重新发起操作。");
            var encrypted = await File.ReadAllBytesAsync(_path, token);
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var saved = JsonSerializer.Deserialize<SavedRequest>(plain, JsonDefaults.Create());
                if (saved is null || saved.ServerId != _server || saved.Operator != _operator || saved.RequestId != _requestId || saved.IntentHash != hash)
                    throw new ArgumentException("该请求标识已用于不同参数；重试时必须保留原输入。");
                Validate(saved.Request); return saved.Request;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        var request = await prepare(); Validate(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedRequest(_server, _operator, _requestId, hash, request), JsonDefaults.Create());
        if (bytes.Length > 512 * 1024) throw new ArgumentException("操作记录过大，未发送请求。");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await output.WriteAsync(protectedBytes, token); await output.FlushAsync(token); output.Flush(true); }
            File.Move(temporary, _path, overwrite: false);
            return request;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Validate(CliBusinessRequest request)
    {
        if (request is null || request.Path is null || !request.Path.StartsWith("/api/v1/", StringComparison.Ordinal) ||
            request.Path.Contains("..", StringComparison.Ordinal) || request.Body.ValueKind != JsonValueKind.Object ||
            !request.Body.TryGetProperty("requestId", out var id) || id.GetString() != _requestId)
            throw new InvalidDataException("原操作记录缺少合法请求，禁止重建或发送。");
    }
    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("操作记录目录不能使用链接或联接。");
    }
    public void Dispose() => _claim.Dispose();
}
