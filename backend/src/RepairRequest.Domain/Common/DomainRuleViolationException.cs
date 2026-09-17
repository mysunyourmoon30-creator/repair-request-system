namespace RepairRequest.Domain.Common;

/// <summary>
/// Raised when a command violates an aggregate-local business rule or state guard
/// (RR-ARCH-001 section 5.1: Domain owns entity invariants and business exceptions).
/// Mapped to a controlled 409/422 response by the API layer in later tickets.
/// </summary>
public sealed class DomainRuleViolationException : Exception
{
    public DomainRuleViolationException(string message)
        : base(message)
    {
    }
}
