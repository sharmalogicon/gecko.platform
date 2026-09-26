namespace Gecko.SharedKernel;

/// <summary>
/// Thrown when an operation would break a domain rule.
///
/// Distinct from ArgumentException / InvalidOperationException on purpose:
/// the API layer maps DomainException to HTTP 409/422 (the caller asked for
/// something the business forbids), while unexpected exceptions map to 500.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a state machine transition is not permitted.
///
/// This is the exception that makes the State Machine pattern in LLD-02 §9.1
/// real rather than aspirational. With public setters, an illegal transition
/// is a silent data corruption discovered weeks later in a delivery report.
/// Here it is a loud, immediate, testable failure at the moment of the bug.
/// </summary>
public sealed class InvalidStateTransitionException : DomainException
{
    public string EntityName { get; }
    public string From { get; }
    public string To { get; }

    public InvalidStateTransitionException(string entityName, string from, string to)
        : base($"{entityName}: illegal transition {from} -> {to}.")
    {
        EntityName = entityName;
        From = from;
        To = to;
    }
}
