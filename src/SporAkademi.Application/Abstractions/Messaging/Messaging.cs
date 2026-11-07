using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Abstractions.Messaging;

/// <summary>A request that changes state. Runs inside a transaction (ADR-0004).</summary>
public interface ICommand<TResponse>;

/// <summary>
/// Marks a command whose expected failures still change state that must be kept, so the transaction
/// commits on a failed result as well: a wrong password counts towards the lockout, a reused refresh
/// token revokes the user's sessions (DEC-113).
/// </summary>
public interface IKeepChangesOnFailure;

/// <summary>
/// Marks a command whose handler also writes outside the database (object storage, DEC-321). The transaction
/// decorator does not wrap it: the handler opens its own <c>IUnitOfWork</c> around the database part only, so a
/// slow storage call never holds a database transaction open, and it undoes its storage write when the database
/// part fails.
/// </summary>
public interface IManagesOwnTransaction;

/// <summary>A read-only request. Projects straight to a DTO; never runs in the transaction decorator.</summary>
public interface IQuery<TResponse>;

/// <summary>Handles one command. Endpoints receive the (decorated) handler directly; there is no dispatcher.</summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Response type for commands that return nothing.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}
