using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.UnitTests.Behaviors;

public sealed class PipelineTests
{
    private readonly CallLog _calls = new();
    private readonly RecordingUnitOfWork _unitOfWork;

    public PipelineTests() => _unitOfWork = new RecordingUnitOfWork { Log = _calls };

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IUnitOfWork>(_unitOfWork);
        services.AddSingleton(_calls);
        services.AddHandlersFrom(typeof(PipelineTests).Assembly);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task Valid_command_runs_inside_a_transaction()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateThing, Guid>>();

        var result = await handler.Handle(new CreateThing("ok"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _calls.Entries.ShouldBe(["transaction:begin", "handler", "transaction:end"]);
    }

    [Fact]
    public async Task Commands_that_keep_changes_on_failure_ask_the_transaction_to_commit_failures()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<AttemptThing, Guid>>();

        var result = await handler.Handle(new AttemptThing(), CancellationToken.None);

        result.Error.Code.ShouldBe("Thing.Failed");
        _calls.Entries.ShouldBe(["transaction:begin(commit-on-failure)", "attempt", "transaction:end"]);
    }

    [Fact]
    public async Task Commands_that_manage_their_own_transaction_are_validated_but_not_wrapped()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<StoreThing, Guid>>();

        (await handler.Handle(new StoreThing("ok"), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await handler.Handle(new StoreThing(""), CancellationToken.None)).Error.Code.ShouldBe("Validation.Failed");
        _calls.Entries.ShouldBe(["store"]);
    }

    [Fact]
    public async Task Invalid_command_never_reaches_the_transaction_or_handler()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateThing, Guid>>();

        var result = await handler.Handle(new CreateThing(""), CancellationToken.None);

        result.Error.Code.ShouldBe("Validation.Failed");
        var errors = result.Error.Arguments[ValidationErrors.ErrorsArgument].ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>()!;
        errors["name"].ShouldBe(["Field.Required"]);
        _calls.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Queries_are_validated_but_not_wrapped_in_a_transaction()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetThing, string>>();

        (await handler.Handle(new GetThing(5), CancellationToken.None)).Value.ShouldBe("thing-5");
        (await handler.Handle(new GetThing(-1), CancellationToken.None)).Error.Code.ShouldBe("Validation.Failed");
        _calls.Entries.ShouldBe(["query"]);
    }

    [Fact]
    public void Decorators_wrap_in_the_documented_order()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var outer = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateThing, Guid>>();

        outer.GetType().GetGenericTypeDefinition().ShouldBe(typeof(LoggingCommandDecorator<,>));
    }

    [Fact]
    public void Default_validator_codes_never_leak()
    {
        var error = ValidationErrors.From([
            new FluentValidation.Results.ValidationFailure("Name", "msg") { ErrorCode = "NotEmptyValidator" },
            new FluentValidation.Results.ValidationFailure("", "msg") { ErrorCode = "Field.Required" },
        ]);

        var errors = (IReadOnlyDictionary<string, string[]>)error.Arguments[ValidationErrors.ErrorsArgument]!;
        errors["name"].ShouldBe(["Field.Invalid"]);
        errors["_"].ShouldBe(["Field.Required"]);
    }

    [Fact]
    public void Single_field_errors_use_the_same_shape()
    {
        var error = ValidationErrors.ForField("email", Error.Validation("Email.Invalid", "d"));

        error.Code.ShouldBe("Validation.Failed");
        ((IReadOnlyDictionary<string, string[]>)error.Arguments[ValidationErrors.ErrorsArgument]!)["email"].ShouldBe(["Email.Invalid"]);
    }

    [Theory]
    [InlineData("Name", "name")]
    [InlineData("Guardians[0].Phone", "guardians[0].phone")]
    [InlineData("emergencyContact.Name", "emergencyContact.name")]
    public void Field_paths_are_camel_cased(string input, string expected)
    {
        ValidationErrors.ToCamelCasePath(input).ShouldBe(expected);
    }
}

public sealed class CallLog
{
    public List<string> Entries { get; } = [];
}

public sealed record CreateThing(string Name) : ICommand<Guid>;

public sealed class CreateThingValidator : AbstractValidator<CreateThing>
{
    public CreateThingValidator() => RuleFor(c => c.Name).NotEmpty().WithErrorCode("Field.Required");
}

public sealed class CreateThingHandler(CallLog calls) : ICommandHandler<CreateThing, Guid>
{
    public Task<Result<Guid>> Handle(CreateThing command, CancellationToken cancellationToken)
    {
        calls.Entries.Add("handler");
        return Task.FromResult(Result.Success(Guid.CreateVersion7()));
    }
}

public sealed record AttemptThing : ICommand<Guid>, IKeepChangesOnFailure;

public sealed class AttemptThingHandler(CallLog calls) : ICommandHandler<AttemptThing, Guid>
{
    public Task<Result<Guid>> Handle(AttemptThing command, CancellationToken cancellationToken)
    {
        calls.Entries.Add("attempt");
        return Task.FromResult(Result.Failure<Guid>(Error.BusinessRule("Thing.Failed", "d")));
    }
}

public sealed record StoreThing(string Name) : ICommand<Guid>, IManagesOwnTransaction;

public sealed class StoreThingValidator : AbstractValidator<StoreThing>
{
    public StoreThingValidator() => RuleFor(c => c.Name).NotEmpty().WithErrorCode("Field.Required");
}

public sealed class StoreThingHandler(CallLog calls) : ICommandHandler<StoreThing, Guid>
{
    public Task<Result<Guid>> Handle(StoreThing command, CancellationToken cancellationToken)
    {
        calls.Entries.Add("store");
        return Task.FromResult(Result.Success(Guid.CreateVersion7()));
    }
}

public sealed record GetThing(int Id) : IQuery<string>;

public sealed class GetThingValidator : AbstractValidator<GetThing>
{
    public GetThingValidator() => RuleFor(q => q.Id).GreaterThan(0).WithErrorCode("Field.Invalid");
}

public sealed class GetThingHandler(CallLog calls) : IQueryHandler<GetThing, string>
{
    public Task<Result<string>> Handle(GetThing query, CancellationToken cancellationToken)
    {
        calls.Entries.Add("query");
        return Task.FromResult(Result.Success($"thing-{query.Id}"));
    }
}

public sealed class RecordingUnitOfWork : IUnitOfWork
{
    public async Task<Result<T>> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, bool commitOnFailure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Log?.Entries.Add(commitOnFailure ? "transaction:begin(commit-on-failure)" : "transaction:begin");
        var result = await operation(cancellationToken);
        Log?.Entries.Add("transaction:end");
        return result;
    }

    public CallLog? Log { get; set; }
}
