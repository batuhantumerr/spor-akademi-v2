namespace SporAkademi.Domain.SharedKernel;

/// <summary>Outcome of an operation that can fail in an expected way (ADR-0007). No implicit conversions.</summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (!isSuccess && error == Error.None)
        {
            throw new ArgumentException("A failure needs an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, bool isSuccess, Error error)
        : base(isSuccess, error) => _value = value;

    /// <summary>The value of a successful result. Reading it on a failure is a programmer error.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error.Code}).");

    internal static Result<T> Success(T value) => new(value, true, Error.None);

    internal static new Result<T> Failure(Error error) => new(default, false, error);
}
