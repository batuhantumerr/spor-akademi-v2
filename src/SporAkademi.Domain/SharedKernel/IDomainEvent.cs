namespace SporAkademi.Domain.SharedKernel;

/// <summary>Something that happened in the domain. Handled in the same transaction, before commit (ADR-0009).</summary>
public interface IDomainEvent;
