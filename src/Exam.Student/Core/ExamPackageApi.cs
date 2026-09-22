using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Student;

namespace Exam.Student.Core;

public interface IExamPackageApi
{
    Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<InstructionsDocument>> GetInstructionsAsync(string preparationId, string sessionId, CancellationToken ct = default);
    IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(ExamResourceDescriptor descriptor, string preparationId, string sessionId, CancellationToken ct = default);
    Task<ApiEnvelope<ParticipationResult>> ConfirmParticipationAsync(ConfirmParticipationRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<ParticipationResult>> WithdrawParticipationAsync(WithdrawParticipationRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<PackageUnlockMaterial>> ResolveUnlockAsync(PackageUnlockRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<CommandAcknowledgement>> AcknowledgeCommandAsync(AcknowledgeCommandRequest request, CancellationToken ct = default);
}

public interface IExamCommandFeed : IAsyncDisposable
{
    Task StartAsync(ParticipationResult participation, Func<CommandDelivery, CancellationToken, Task> handler, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
