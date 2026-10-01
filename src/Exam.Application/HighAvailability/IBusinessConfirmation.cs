namespace Exam.Application.HighAvailability;

/// <summary>Called after persistence and before any successful response or reliable push.</summary>
public interface IBusinessConfirmation
{
    Task ConfirmAsync(CancellationToken cancellationToken);
}
