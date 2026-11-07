using System.Diagnostics;
using FluentValidation;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Abstractions.Behaviors;

// Decorator order, outermost first: Logging -> Validation -> Transaction (commands only) -> handler.
// Logs carry only the request type name and error codes, never request contents (NFR-PRV-03).

internal sealed class LoggingCommandDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner,
    ILogger<LoggingCommandDecorator<TCommand, TResponse>> logger) : ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken) =>
        RequestLogging.Run(logger, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken));
}

internal sealed class LoggingQueryDecorator<TQuery, TResponse>(
    IQueryHandler<TQuery, TResponse> inner,
    ILogger<LoggingQueryDecorator<TQuery, TResponse>> logger) : IQueryHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    public Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken) =>
        RequestLogging.Run(logger, typeof(TQuery).Name, () => inner.Handle(query, cancellationToken));
}

internal sealed class ValidationCommandDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await RequestValidation.ValidateAsync(command, validators, cancellationToken);
        return error is null ? await inner.Handle(command, cancellationToken) : Result.Failure<TResponse>(error);
    }
}

internal sealed class ValidationQueryDecorator<TQuery, TResponse>(
    IQueryHandler<TQuery, TResponse> inner,
    IEnumerable<IValidator<TQuery>> validators) : IQueryHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var error = await RequestValidation.ValidateAsync(query, validators, cancellationToken);
        return error is null ? await inner.Handle(query, cancellationToken) : Result.Failure<TResponse>(error);
    }
}

internal sealed class TransactionCommandDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner,
    IUnitOfWork unitOfWork) : ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken) =>
        command is IManagesOwnTransaction
            ? inner.Handle(command, cancellationToken)
            : unitOfWork.ExecuteInTransactionAsync(
                ct => inner.Handle(command, ct),
                commitOnFailure: command is IKeepChangesOnFailure,
                cancellationToken);
}

internal static partial class RequestLogging
{
    public static async Task<Result<T>> Run<T>(ILogger logger, string requestName, Func<Task<Result<T>>> next)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await next();
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (result.IsSuccess)
        {
            LogSucceeded(logger, requestName, elapsedMs);
        }
        else
        {
            LogFailed(logger, requestName, result.Error.Code, elapsedMs);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{RequestName} succeeded in {ElapsedMs:0.0} ms")]
    private static partial void LogSucceeded(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "{RequestName} failed with {ErrorCode} in {ElapsedMs:0.0} ms")]
    private static partial void LogFailed(ILogger logger, string requestName, string errorCode, double elapsedMs);
}

internal static class RequestValidation
{
    public static async Task<Error?> ValidateAsync<T>(
        T request,
        IEnumerable<IValidator<T>> validators,
        CancellationToken cancellationToken)
    {
        var context = new ValidationContext<T>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            failures.AddRange(result.Errors);
        }

        return failures.Count == 0 ? null : ValidationErrors.From(failures);
    }
}
