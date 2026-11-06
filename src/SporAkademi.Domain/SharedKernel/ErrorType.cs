namespace SporAkademi.Domain.SharedKernel;

/// <summary>Kinds of expected failure; each maps to one HTTP status (ADR-0007).</summary>
public enum ErrorType
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    BusinessRule,
}
