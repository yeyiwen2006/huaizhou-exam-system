namespace Exam.Server.Runtime;

public sealed class ServerIdentityOptions
{
    public const string SectionName = "ExamServer";

    public string ServerId { get; init; } = string.Empty;

    public string NodeId { get; init; } = string.Empty;

    public long ServerEpoch { get; init; }

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ServerId) &&
        !string.IsNullOrWhiteSpace(NodeId) &&
        ServerEpoch > 0;
}

public sealed record ServerTimeResponse(DateTimeOffset ServerTime);
