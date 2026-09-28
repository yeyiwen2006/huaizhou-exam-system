using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Student;

namespace Exam.Student.Core;

internal sealed record ProcessedPackageCommand(
    CommandDelivery Delivery,
    AcknowledgeCommandRequest Acknowledgement,
    bool Acknowledged, DateTimeOffset? ReceivedAt = null);

internal sealed record PaperUpdateInstallation(long PaperVersion, DateTimeOffset ReceivedAt, DateTimeOffset InstalledAt);

internal sealed record PaperUpdateTransfer(PrepareExamEntryRequest PrepareRequest,
    string ParticipationId, long EntryVersion, long AuthorizationVersion, long SourcePaperVersion, long RequestedVersion,
    EntryPreparationResult? Preparation = null, InstructionsDocument? Instructions = null,
    ConfirmParticipationRequest? ConfirmRequest = null, ParticipationResult? Participation = null,
    PackageUnlockRequest? UnlockRequest = null);

internal sealed record PendingPaperInstallation(long PreviousPaperVersion, long MinimumAnswerVersion,
    string SnapshotRequestId, EntryPreparationResult Preparation, string SessionId,
    InstructionsDocument Instructions, ParticipationResult Participation, byte[] ProtectedUnlockMaterial,
    PaperUpdateInstallation Installation);

internal sealed record PackageControlState
{
    public int FormatVersion { get; init; } = 2;
    public required string ServerId { get; init; }
    public required string DeviceId { get; init; }
    public required string CandidateId { get; init; }
    public required string ExamId { get; init; }
    public ServerNodeEnvelope? Server { get; init; }
    public PrepareExamEntryRequest? PendingPrepare { get; init; }
    public EntryPreparationResult? Preparation { get; init; }
    public string? PreparationSessionId { get; init; }
    public long? CompletedPaperVersion { get; init; }
    public string? CompletedPackageUri { get; init; }
    public InstructionsDocument? Instructions { get; init; }
    public ConfirmParticipationRequest? PendingConfirm { get; init; }
    public WithdrawParticipationRequest? PendingWithdraw { get; init; }
    public ParticipationResult? Participation { get; init; }
    public byte[]? ProtectedUnlockMaterial { get; init; }
    public bool Started { get; init; }
    public bool Ended { get; init; }
    public bool Revoked { get; init; }
    public long LifecycleStateVersion { get; init; }
    public long NewestPaperVersion { get; init; }
    public PaperUpdateTransfer? UpdateTransfer { get; init; }
    public PendingPaperInstallation? PendingPaperInstallation { get; init; }
    public PaperUpdateInstallation? LastPaperInstallation { get; init; }
    public IReadOnlyList<ProcessedPackageCommand> Commands { get; init; } = [];
}

/// <summary>
/// B02 control recovery only. Each logical server/candidate/exam has one locked,
/// current-user DPAPI document; answer snapshots are owned by B04.
/// </summary>
internal sealed class PackageControlStore : IDisposable
{
    private const int MaximumStateBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Create();
    private readonly byte[] _entropy;
    private readonly FileStream _access;
    private readonly string _statePath;
    private bool _disposed;

    public PackageControlStore(string root, string serverId, string deviceId, string candidateId, string examId)
    {
        Root = Path.Combine(Path.GetFullPath(root), Segment(serverId), Segment(candidateId), Segment(examId));
        Directory.CreateDirectory(Root);
        _statePath = Path.Combine(Root, "control.protected");
        _entropy = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
        {
            "HuaizhouExam.Student.PackageControl.v1", serverId, deviceId, candidateId, examId
        }));
        _access = new FileStream(Path.Combine(Root, "control.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public string Root { get; }

    public string PackagePath(long version) => version > 0
        ? Path.Combine(Root, version.ToString(System.Globalization.CultureInfo.InvariantCulture), "package.epkg")
        : throw new ArgumentOutOfRangeException(nameof(version));

    public PackageControlState Load(PackageControlState empty)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(_statePath))
        {
            if (File.Exists(_statePath + ".bak"))
            {
                throw new IOException("考试恢复记录需要人工恢复，不能覆盖已有备份。");
            }

            return empty;
        }

        if (new FileInfo(_statePath).Length > MaximumStateBytes)
            throw new InvalidDataException("考试恢复记录超过安全读取上限，原文件已保留。");
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(_statePath), _entropy, DataProtectionScope.CurrentUser);
        try
        {
            var state = JsonSerializer.Deserialize<PackageControlState>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("考试恢复记录为空。");
            if (state.FormatVersion is not (1 or 2) || state.ServerId != empty.ServerId || state.DeviceId != empty.DeviceId ||
                state.CandidateId != empty.CandidateId || state.ExamId != empty.ExamId || state.Commands is null ||
                state.Commands.Count > 4096 || state.Commands.Select(command => command.Delivery.EventId)
                    .Distinct(StringComparer.Ordinal).Count() != state.Commands.Count)
            {
                throw new InvalidDataException("考试恢复记录身份或格式不一致，原文件已保留。");
            }

            return state;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Save(PackageControlState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state with { FormatVersion = 2 }, JsonOptions);
        byte[] encrypted;
        try
        {
            if (plaintext.Length > MaximumStateBytes - 4096)
                throw new IOException("考试恢复记录已达存储上限，请联系考务人员。");
            encrypted = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var temporary = Path.Combine(Root, $"control-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_statePath))
            {
                File.Replace(temporary, _statePath, _statePath + ".bak");
            }
            else
            {
                File.Move(temporary, _statePath);
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

    public byte[] ProtectUnlock(PackageUnlockMaterial material)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(material, JsonOptions);
        try
        {
            return ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string Segment(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("考试存储身份不能为空。", nameof(identifier));
        }

        // Fixed-size safe path component, independent of externally supplied slashes or drive names.
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _access.Dispose();
        }
    }
}
