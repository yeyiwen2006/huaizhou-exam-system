namespace Exam.Contracts.HighAvailability;

// Contains no credentials, answers, paths or operator supplied evidence flags.
public sealed record DurableConfirmation(string Id, string ServerId, string PrimaryNodeId,
    string StandbyNodeId, long Epoch, long StateVersion, ProtectionMode Mode,
    string SystemIdentifier, string DatabaseName, int Timeline, string FlushLsn, string ResourcesDigest);
