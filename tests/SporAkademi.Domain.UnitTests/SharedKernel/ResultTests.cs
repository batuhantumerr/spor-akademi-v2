using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.SharedKernel;

public sealed class ResultTests
{
    private static readonly Error SampleError = Error.Validation("Sample.Invalid", "Sample is invalid.");

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_carries_the_error()
    {
        var result = Result.Failure(SampleError);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SampleError);
    }

    [Fact]
    public void Failure_with_none_is_a_programmer_error()
    {
        Should.Throw<ArgumentException>(() => Result.Failure(Error.None));
    }

    [Fact]
    public void Generic_success_exposes_the_value()
    {
        var result = Result.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Reading_the_value_of_a_failure_throws()
    {
        var result = Result.Failure<int>(SampleError);

        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.BusinessRule)]
    public void Error_factories_set_the_type(ErrorType type)
    {
        var error = type switch
        {
            ErrorType.Validation => Error.Validation("X.Y", "d"),
            ErrorType.Unauthorized => Error.Unauthorized("X.Y", "d"),
            ErrorType.Forbidden => Error.Forbidden("X.Y", "d"),
            ErrorType.NotFound => Error.NotFound("X.Y", "d"),
            ErrorType.Conflict => Error.Conflict("X.Y", "d"),
            _ => Error.BusinessRule("X.Y", "d"),
        };

        error.Type.ShouldBe(type);
        error.Code.ShouldBe("X.Y");
    }

    [Fact]
    public void Error_can_carry_message_arguments()
    {
        var error = Error.BusinessRule("Group.CapacityBelowActiveEnrollments", "d")
            .WithArgument("count", 12);

        error.Arguments.ShouldContainKeyAndValue("count", 12);
    }
}

public sealed class ErrorTests
{
    [Fact]
    public void Errors_are_equal_by_code_and_type()
    {
        Error.Conflict("A.B", "one").ShouldBe(Error.Conflict("A.B", "two"));
        Error.Conflict("A.B", "d").ShouldNotBe(Error.BusinessRule("A.B", "d"));
    }

    [Fact]
    public void With_argument_returns_a_new_error_and_keeps_the_original()
    {
        var original = Error.BusinessRule("A.B", "d");

        var withArgument = original.WithArgument("count", 3);

        original.Arguments.ShouldBeEmpty();
        withArgument.Arguments["count"].ShouldBe(3);
    }

    [Fact]
    public void Generic_failure_with_none_is_a_programmer_error()
    {
        Should.Throw<ArgumentException>(() => Result.Failure<int>(Error.None));
    }

    [Fact]
    public void Blank_codes_are_programmer_errors()
    {
        Should.Throw<ArgumentException>(() => Error.Validation(" ", "d"));
    }
}
