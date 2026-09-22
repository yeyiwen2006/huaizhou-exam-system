using System.IO;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;

namespace Exam.Student.Core;

/// <summary>下载进度（页面展示用）。</summary>
public sealed record PackageDownloadProgress(long ReceivedBytes, long TotalBytes);

/// <summary>考试说明文档（Mock 展示层；说明下发契约缺口见 docs/b01-implementation-status.md）。</summary>
public sealed record InstructionsDocument(long Version, IReadOnlyList<string> Lines);

/// <summary>
/// 考试包管理器：B02 核心状态机。
/// 状态流：下载中 → 已下载（缺包/缺说明时不可确认参试）→ 已确认等待 →（收到开考指令或已在答题态）解锁。
/// 安全约束：
///  1. 磁盘上只有加密包，题目明文仅存在于解锁成功后的内存；
///  2. 未收到开考指令（或服务器未告知已进入答题态）前不尝试解密；
///  3. 考试结束后（含下载期间结束）不可再确认参试；
///  4. 下载通过临时文件 + 原子重命名落盘，中断不留半截文件。
/// </summary>
public sealed class ExamPackageManager
{
    private readonly IStudentApi _api;
    private readonly string _storageRoot;

    public ExamPackageManager(IStudentApi api, string storageRoot)
    {
        _api = api;
        _storageRoot = storageRoot;
        Directory.CreateDirectory(storageRoot);
    }

    public enum Phase
    {
        Idle,
        Downloading,
        Downloaded,
        ConfirmedWaiting,
        Unlocked,
        Ended,
    }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;

    /// <summary>加密包是否已完整落盘。</summary>
    public bool HasPackage { get; private set; }

    /// <summary>考试说明是否已获取。</summary>
    public bool HasInstructions { get; private set; }

    public ExamResourceDescriptor? Descriptor { get; private set; }
    public InstructionsDocument? Instructions { get; private set; }
    public string? ParticipationId { get; private set; }
    public string? UnlockReference { get; private set; }

    /// <summary>解锁后的题目明文（仅内存；开考前恒为 null）。</summary>
    public byte[]? UnlockedPayload { get; private set; }

    /// <summary>服务器或本地时钟判定考试已结束（结束后不可确认参试）。</summary>
    public bool ExamEnded { get; private set; }

    /// <summary>考试是否已开始（开考指令驱动，或服务器告知已在答题态）。</summary>
    public bool ExamStarted { get; private set; }

    /// <summary>加密包落盘路径（文件内容始终为密文）。</summary>
    public string PackageFilePath =>
        Path.Combine(_storageRoot, $"{Descriptor?.PaperVersion.ToString() ?? "0"}.pkg");

    /// <summary>是否允许确认参试：包与说明齐备、尚未结束、尚未确认。</summary>
    public bool CanConfirmParticipation =>
        HasPackage && HasInstructions && !ExamEnded &&
        CurrentPhase is Phase.Downloaded or Phase.Idle;

    /// <summary>
    /// 下载加密包与考试说明。二者任一失败即视为准备未完成（不可确认参试）。
    /// </summary>
    public async Task<bool> DownloadAsync(
        string sessionId,
        string examId,
        ExamResourceDescriptor descriptor,
        IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (CurrentPhase is Phase.ConfirmedWaiting or Phase.Unlocked)
        {
            throw new InvalidOperationException("已确认参试，不能重复下载。");
        }

        CurrentPhase = Phase.Downloading;
        HasPackage = false;
        Descriptor = descriptor;

        var packageOk = await DownloadPackageAtomicAsync(descriptor, progress, ct);
        if (!packageOk)
        {
            HasInstructions = false;
            return false;
        }

        HasPackage = true;

        var instructions = await _api.GetInstructionsAsync(sessionId, examId, descriptor.InstructionsVersion, ct);
        if (instructions is null)
        {
            HasInstructions = false;
            return false;
        }

        Instructions = instructions;
        HasInstructions = true;
        CurrentPhase = Phase.Downloaded;
        return true;
    }

    /// <summary>标记考试结束（服务器结束指令或等待页超时）。</summary>
    public void OnExamEnded()
    {
        ExamEnded = true;
        if (CurrentPhase is Phase.ConfirmedWaiting or Phase.Downloaded or Phase.Downloading)
        {
            CurrentPhase = Phase.Ended;
        }
    }

    /// <summary>
    /// 确认参试（前提：CanConfirmParticipation）。服务器同时校验下载版本，
    /// 返回失败（如考试已结束）时不进入等待状态。
    /// </summary>
    public async Task<ApiEnvelope<ParticipationResult>?> ConfirmParticipationAsync(
        string sessionId,
        string examId,
        CancellationToken ct = default)
    {
        if (!CanConfirmParticipation)
        {
            return null;
        }

        if (Descriptor is null || Instructions is null)
        {
            return null;
        }

        var request = new ConfirmParticipationRequest(
            RequestId: RequestIds.New(),
            SessionId: sessionId,
            ExamId: examId,
            PreparationId: _preparationId ?? "prep-unknown",
            DownloadedPaperVersion: Descriptor.PaperVersion,
            DownloadedInstructionsVersion: Instructions.Version);

        var result = await _api.ConfirmParticipationAsync(request, ct);
        if (result?.IsSuccess == true && result.Result is not null)
        {
            ParticipationId = result.Result.ParticipationId;
            UnlockReference = result.Result.UnlockReference;
            CurrentPhase = Phase.ConfirmedWaiting;

            // 已开始的考试（迟到进入）：服务器直接告知答题态，无需等待开考指令
            if (result.Result.BusinessState == ParticipationBusinessState.Answering)
            {
                NotifyExamStarted();
            }
        }

        return result;
    }

    /// <summary>等待页退出参试（服务器要求退出确认；失败由调用方提示重试）。</summary>
    public Task<ApiEnvelope<ParticipationResult>?> WithdrawAsync(
        string sessionId,
        long expectedVersion,
        CancellationToken ct = default)
    {
        if (ParticipationId is null)
        {
            return Task.FromResult<ApiEnvelope<ParticipationResult>?>(null);
        }

        return _api.WithdrawParticipationAsync(new WithdrawParticipationRequest(
            RequestId: RequestIds.New(),
            SessionId: sessionId,
            ParticipationId: ParticipationId,
            ExpectedVersion: expectedVersion), ct);
    }

    /// <summary>标记考试开始（开考指令到达）。</summary>
    public void NotifyExamStarted() => ExamStarted = true;

    /// <summary>
    /// 收到服务器开考指令（CommandDelivery，EventType=Start）：
    /// 标记开始、记录解锁引用并在已确认参试的前提下解锁。
    /// </summary>
    public async Task<bool> HandleStartCommandAsync(CommandDelivery delivery, CancellationToken ct = default)
    {
        if (delivery.EventType != CommandEventType.Start)
        {
            return false;
        }

        if (CurrentPhase != Phase.ConfirmedWaiting)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(delivery.ProtectedUnlockReference))
        {
            UnlockReference = delivery.ProtectedUnlockReference;
        }

        NotifyExamStarted();
        return await UnlockAsync(ct);
    }

    /// <summary>
    /// 解锁：以解锁引用向服务器换取内容密钥并解密（明文仅驻内存）。
    /// 只有已确认参试、考试已开始（开考指令或服务器告知答题态）、持有解锁引用才允许——
    /// 等待状态下即使提前拿到解锁引用也不解密（开考前无题目明文的硬约束）。
    /// </summary>
    public async Task<bool> UnlockAsync(CancellationToken ct = default)
    {
        if (!ExamStarted || CurrentPhase != Phase.ConfirmedWaiting || !HasPackage || UnlockReference is null)
        {
            return false;
        }

        if (!File.Exists(PackageFilePath))
        {
            return false;
        }

        var key = await _api.ResolveUnlockKeyAsync(UnlockReference, ct);
        if (key is null || key.Length == 0)
        {
            return false;
        }

        var package = await File.ReadAllBytesAsync(PackageFilePath, ct);
        if (!SyntheticExamPackage.TryUnseal(package, key, out var plaintext))
        {
            return false;
        }

        UnlockedPayload = plaintext;
        CurrentPhase = Phase.Unlocked;
        return true;
    }

    private async Task<bool> DownloadPackageAtomicAsync(
        ExamResourceDescriptor descriptor,
        IProgress<PackageDownloadProgress>? progress,
        CancellationToken ct)
    {
        var finalPath = PackageFilePath;
        var tempPath = finalPath + ".part";
        try
        {
            await using (var target = File.Create(tempPath))
            {
                await foreach (var chunk in _api.DownloadPackageChunksAsync(descriptor, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    await target.WriteAsync(chunk, ct);
                    progress?.Report(new PackageDownloadProgress(target.Length, descriptor.PackageLength));
                }

                await target.FlushAsync(ct);
            }

            // 长度校验（完整性；D03 定稿后改为哈希/签名校验）
            var written = new FileInfo(tempPath).Length;
            if (descriptor.PackageLength > 0 && written != descriptor.PackageLength)
            {
                throw new IOException($"试题包长度不符（预期 {descriptor.PackageLength}，实际 {written}）。");
            }

            // 原子替换：只有完整下载并校验通过才出现最终文件
            File.Move(tempPath, finalPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception)
        {
            // 下载失败（超时/网络/校验）：清理半截文件，保持“无包”状态
            TryDelete(tempPath);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private string? _preparationId;

    /// <summary>记录准备标识（服务器退出/版本核对用）。</summary>
    public void SetPreparation(string preparationId) => _preparationId = preparationId;
}
