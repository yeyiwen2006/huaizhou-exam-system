namespace Exam.Application.Activation;

public sealed class ActivationRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
