namespace DocuMind.Domain.Common;

/// <summary>Raised when an operation would violate a domain invariant.</summary>
public sealed class DomainException(string message) : Exception(message);
