namespace SporAkademi.Domain.SharedKernel;

/// <summary>Field-level validation errors shared by every module (appendix E).</summary>
public static class CommonErrors
{
    public static readonly Error FieldRequired = Error.Validation("Field.Required", "The field is required.");

    public static readonly Error FieldTooLong = Error.Validation("Field.TooLong", "The field is too long.");

    public static readonly Error FieldInvalid = Error.Validation("Field.Invalid", "The field value is not valid.");
}
