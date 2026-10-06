using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Activation;

namespace Exam.Student.Core;

/// <summary>设备身份与未完成身份请求。撤销权限不会删除设备身份。</summary>
public sealed record DeviceRecord(
    string DeviceId,
    string Credential,
    string? ClientNumber,
    string Host,
    int Port,
    DateTimeOffset ActivatedAt,
    string? ServerId = null,
    ActivationStatus Status = ActivationStatus.Pending,
    string? ActivationRequestId = null,
    string? SubmitRequestId = null,
    string? PendingOperationRequestId = null,
    string? PendingOperation = null,
    long CredentialVersion = 0,
    long ServerEpoch = 0)
{
    public TrustedServerTopology? Topology { get; init; }
    public string? ActiveNodeId { get; init; }
}

public sealed class DeviceStoreException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>
/// 当前 Windows 用户的受保护身份库。整份库加密后原子替换，旧版保留为同样受保护的备份。
/// 每个服务器地址保存独立身份；服务器逻辑标识一经确认不可被响应替换。
/// </summary>
public sealed class DeviceStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HuaizhouExam.Student.DeviceStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _directory;
    private readonly object _gate = new();

    public DeviceStore(string directory) => _directory = Path.GetFullPath(directory);

    private string FilePath => Path.Combine(_directory, "devices.protected");

    public DeviceRecord? Load() => Access(document => document.LastEndpoint is null
        ? null
        : document.Records.Single(record => EndpointKey(record.Host, record.Port) == document.LastEndpoint));

    public DeviceRecord? Load(string host, int port)
    {
        var endpoint = EndpointKey(host, port);
        return Access(document => document.Records.SingleOrDefault(record => EndpointKey(record.Host, record.Port) == endpoint ||
            record.Topology?.Nodes.Any(node => EndpointKey(node.Host, node.Port) == endpoint) == true));
    }

    public void Save(DeviceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);
        Access(document =>
        {
            var endpoint = EndpointKey(record.Host, record.Port);
            var existing = document.Records.SingleOrDefault(item => EndpointKey(item.Host, item.Port) == endpoint);
            if (existing is not null &&
                (existing.DeviceId != record.DeviceId || existing.Credential != record.Credential ||
                 (existing.ServerId is not null && existing.ServerId != record.ServerId) ||
                 (existing.ClientNumber is not null && existing.ClientNumber != record.ClientNumber) ||
                 (existing.SubmitRequestId is not null && existing.SubmitRequestId != record.SubmitRequestId) ||
                 (existing.ActivationRequestId is not null && existing.ActivationRequestId != record.ActivationRequestId) ||
                 (existing.PendingOperationRequestId is not null && record.PendingOperationRequestId is not null &&
                     (existing.PendingOperationRequestId != record.PendingOperationRequestId || existing.PendingOperation != record.PendingOperation)) ||
                 existing.ServerEpoch > record.ServerEpoch || existing.CredentialVersion > record.CredentialVersion ||
                 existing.Topology is { } topology && (record.Topology is null || !topology.Equivalent(record.Topology))))
            {
                throw new DeviceStoreException("设备身份或服务器身份与已有登记不一致，原记录已保留。请联系考务人员恢复。");
            }

            var records = document.Records.Where(item => EndpointKey(item.Host, item.Port) != endpoint).Append(record).ToArray();
            Write(new DeviceDocument(1, endpoint, records));
            return true;
        });
    }

    public static string NormalizeHost(string host)
    {
        if (!IPAddress.TryParse(host.Trim(), out var address))
        {
            throw new ArgumentException("服务器 IP 地址格式不正确。", nameof(host));
        }

        return address.ToString();
    }

    private static string EndpointKey(string host, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return $"[{NormalizeHost(host)}]:{port}";
    }

    private T Access<T>(Func<DeviceDocument, T> action)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                using var access = new FileStream(Path.Combine(_directory, "devices.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return action(Read());
            }
            catch (DeviceStoreException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException or ArgumentException)
            {
                throw new DeviceStoreException("无法读取或保存受保护的设备身份。原记录已保留，请检查磁盘、当前 Windows 账户或联系考务人员恢复。", error);
            }
        }
    }

    private DeviceDocument Read()
    {
        if (!File.Exists(FilePath))
        {
            if (File.Exists(Path.Combine(_directory, "device.json")) || File.Exists(FilePath + ".bak"))
            {
                throw new DeviceStoreException("检测到旧版或待恢复的设备身份文件，不能创建新身份覆盖登记。请联系考务人员恢复，原文件已保留。");
            }

            return new DeviceDocument(1, null, []);
        }

        var encrypted = File.ReadAllBytes(FilePath);
        var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var document = JsonSerializer.Deserialize<DeviceDocument>(plaintext, JsonOptions)
                ?? throw new DeviceStoreException("设备身份文件内容为空，请保留原文件并联系考务人员恢复。");
            if (document.FormatVersion != 1 || document.Records is null)
            {
                throw new DeviceStoreException("设备身份文件格式不受支持，请保留原文件并联系考务人员恢复。");
            }

            foreach (var record in document.Records)
            {
                ValidateRecord(record);
            }

            if (document.Records.Select(record => EndpointKey(record.Host, record.Port)).Distinct(StringComparer.Ordinal).Count() != document.Records.Count ||
                (document.LastEndpoint is not null && !document.Records.Any(record => EndpointKey(record.Host, record.Port) == document.LastEndpoint)))
            {
                throw new DeviceStoreException("设备身份索引损坏，请保留原文件并联系考务人员恢复。");
            }

            return document;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Write(DeviceDocument document)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var temporary = Path.Combine(_directory, $"devices.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
            {
                File.Replace(temporary, FilePath, FilePath + ".bak");
            }
            else
            {
                File.Move(temporary, FilePath);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ValidateRecord(DeviceRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.DeviceId) || string.IsNullOrWhiteSpace(record.Credential) ||
            !Enum.IsDefined(record.Status) || record.CredentialVersion < 0 || record.ServerEpoch < 0 ||
            (record.Status == ActivationStatus.Active && (string.IsNullOrWhiteSpace(record.ClientNumber) || string.IsNullOrWhiteSpace(record.ServerId))))
        {
            throw new DeviceStoreException("设备身份内容不完整，不能作为新设备继续。请联系考务人员恢复。");
        }

        _ = EndpointKey(record.Host, record.Port);
        if (record.Topology is { } topology)
        {
            try { topology.Validate(); }
            catch (FormatException error) { throw new DeviceStoreException("受保护的主备信任信息损坏，原文件已保留。", error); }
            if (record.ServerId != topology.ServerId ||
                !topology.Nodes.Any(node => node.Address == new UriBuilder(Uri.UriSchemeHttps, record.Host, record.Port).Uri) ||
                record.ActiveNodeId is not null && !topology.Nodes.Any(node => node.NodeId == record.ActiveNodeId))
                throw new DeviceStoreException("受保护的主备信任信息与设备登记不一致。");
        }
    }

    private sealed record DeviceDocument(int FormatVersion, string? LastEndpoint, IReadOnlyList<DeviceRecord> Records);
}
