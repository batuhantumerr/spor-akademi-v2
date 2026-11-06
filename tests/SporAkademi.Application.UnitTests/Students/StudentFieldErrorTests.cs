using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Students;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.UnitTests.Students;

[Trait("Story", "US-13")]
public sealed class StudentFieldErrorTests
{
    private static RegisterStudentCommand Input(string firstName = "Ali", string lastName = "Deneme", Gender gender = Gender.Male) =>
        new(firstName, lastName, new DateOnly(2015, 5, 1), gender, null, null, null, null);

    [Theory]
    [InlineData("", "Deneme", Gender.Male, "Field.Required", "firstName")]
    [InlineData("Ali", " ", Gender.Male, "Field.Required", "lastName")]
    [InlineData("Ali", "Deneme", (Gender)7, "Field.Invalid", "gender")]
    [InlineData("Ali", "Deneme", Gender.Female, "Field.Invalid", "birthDate")]
    [InlineData("Ali", "Deneme", Gender.Male, "Student.EmergencyContactRequired", "emergencyContact")]
    public void Field_errors_name_the_field_they_concern(string firstName, string lastName, Gender gender, string code, string field)
    {
        var error = new[] { CommonErrors.FieldRequired, CommonErrors.FieldInvalid, StudentErrors.EmergencyContactRequired }.Single(e => e.Code == code);

        var mapped = error.ToStudentFieldError(Input(firstName, lastName, gender));

        mapped.Code.ShouldBe(ValidationErrors.Code);
        var errors = (IDictionary<string, string[]>)mapped.Arguments[ValidationErrors.ErrorsArgument]!;
        errors[field].ShouldBe([code]);
    }

    [Fact]
    public void A_too_long_last_name_is_reported_on_the_last_name()
    {
        var mapped = CommonErrors.FieldTooLong.ToStudentFieldError(Input(lastName: new string('a', 101)));

        ((IDictionary<string, string[]>)mapped.Arguments[ValidationErrors.ErrorsArgument]!).Keys.ShouldBe(["lastName"]);
    }

    [Fact]
    public void Rule_failures_pass_through()
    {
        StudentErrors.GuardianRequired.ToStudentFieldError(Input()).ShouldBe(StudentErrors.GuardianRequired);
    }
}
