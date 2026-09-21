namespace Exam.Application.Participation;

public sealed class ParticipationRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
