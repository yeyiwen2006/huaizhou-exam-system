using Exam.Contracts.Versioning;

namespace Exam.Domain.Versioning;

public sealed class VersioningRuleException : Exception
{
    public VersioningRuleException(VersioningErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public VersioningErrorCode Code { get; }
}
