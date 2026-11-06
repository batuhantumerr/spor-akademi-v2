using FluentValidation.Results;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Abstractions.Behaviors;

/// <summary>Builds the <c>Validation.Failed</c> error whose <c>errors</c> argument maps field paths to error codes.</summary>
public static class ValidationErrors
{
    public const string Code = "Validation.Failed";
    public const string ErrorsArgument = "errors";

    public static Error From(IEnumerable<ValidationFailure> failures)
    {
        var errors = failures
            .GroupBy(f => ToCamelCasePath(f.PropertyName), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => f.ErrorCode).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return Error.Validation(Code, "One or more fields are invalid.").WithArgument(ErrorsArgument, errors);
    }

    /// <summary>Converts a single field error (e.g. a value object failure) into the same shape.</summary>
    public static Error ForField(string field, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [error.Code] };
        return Error.Validation(Code, "One or more fields are invalid.").WithArgument(ErrorsArgument, errors);
    }

    internal static string ToCamelCasePath(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(segment =>
            segment.Length == 0 || char.IsLower(segment[0])
                ? segment
                : string.Concat(char.ToLowerInvariant(segment[0]).ToString(), segment[1..])));
}
