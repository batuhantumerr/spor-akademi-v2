using System.Text.Json;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Infrastructure.Outbox;

namespace SporAkademi.IntegrationTests.Outbox;

/// <summary>Pure tests (no database) for the outbox's backoff, serialization and type registry.</summary>
public sealed class OutboxPolicyTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(5, 160)]
    [InlineData(6, 300)] // 320 s capped at 5 min
    [InlineData(1000, 300)] // no overflow for huge attempt counts
    public void Backoff_doubles_per_failure_up_to_the_cap_with_jitter_between_half_and_full(int failures, int ceilingSeconds)
    {
        var ceiling = TimeSpan.FromSeconds(ceilingSeconds);

        OutboxRetryPolicy.Delay(failures, Base, Max, 0).ShouldBe(ceiling / 2);
        OutboxRetryPolicy.Delay(failures, Base, Max, 0.5).ShouldBe(ceiling * 0.75);
        OutboxRetryPolicy.Delay(failures, Base, Max, 0.999_999).ShouldBeLessThan(ceiling);
        OutboxRetryPolicy.Delay(failures, Base, Max, 0.999_999).ShouldBeGreaterThan(ceiling * 0.99);
    }

    [Fact]
    public void Backoff_rejects_nonsense_input()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => OutboxRetryPolicy.Delay(0, Base, Max, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => OutboxRetryPolicy.Delay(1, Base, Max, 1));
    }

    [Fact]
    public void Messages_round_trip_as_camel_case_json_under_their_registered_name()
    {
        var types = new OutboxMessageTypes([new OutboxMessageRegistration(typeof(StudentLeftMessage))]);
        var message = new StudentLeftMessage(Guid.Parse("0199a2b3-0000-7000-8000-000000000001"), new DateOnly(2027, 1, 10));

        var type = types.Get(typeof(StudentLeftMessage));
        var json = type.Serialize(message);

        type.Name.ShouldBe("students.student-left.v1");
        json.ShouldBe("""{"studentId":"0199a2b3-0000-7000-8000-000000000001","leftOn":"2027-01-10"}""");
        types.TryGet("students.student-left.v1", out var stored).ShouldBeTrue();
        stored.Deserialize(json).ShouldBe(message);
    }

    [Fact]
    public void Enums_are_stored_by_name()
    {
        var type = OutboxMessageType.Create(typeof(ProbeMessage));

        var json = type.Serialize(new ProbeMessage(Guid.Empty, "x", ProbeKind.Second));

        json.ShouldContain("\"kind\":\"Second\"");
        ((ProbeMessage)type.Deserialize(json)).Kind.ShouldBe(ProbeKind.Second);
    }

    [Theory]
    [InlineData("""{"studentId":"0199a2b3-0000-7000-8000-000000000001"}""")] // missing field
    [InlineData("""{"studentId":"not-a-guid","leftOn":"2027-01-10"}""")]
    [InlineData("null")]
    [InlineData("not json")]
    public void Payloads_that_do_not_match_the_message_are_rejected(string payload)
    {
        var type = OutboxMessageType.Create(typeof(StudentLeftMessage));

        Should.Throw<JsonException>(() => type.Deserialize(payload));
    }

    [Fact]
    public void Names_from_the_database_resolve_only_to_registered_types()
    {
        var types = new OutboxMessageTypes([new OutboxMessageRegistration(typeof(StudentLeftMessage))]);

        types.TryGet(typeof(StudentLeftMessage).AssemblyQualifiedName!, out _).ShouldBeFalse();
        types.TryGet(typeof(StudentLeftMessage).FullName!, out _).ShouldBeFalse();
        types.TryGet("students.student-left.v2", out _).ShouldBeFalse();
    }

    [Fact]
    public void Message_types_need_a_unique_versioned_name()
    {
        Should.Throw<InvalidOperationException>(() => OutboxMessageType.Create(typeof(UnnamedMessage)));
        Should.Throw<InvalidOperationException>(() => OutboxMessageType.Create(typeof(UnversionedMessage)));
        Should.Throw<InvalidOperationException>(() => new OutboxMessageTypes(
            [new OutboxMessageRegistration(typeof(ProbeMessage)), new OutboxMessageRegistration(typeof(ProbeDuplicateMessage))]));
    }

    private sealed record UnnamedMessage(Guid Id) : IOutboxMessage;

    [OutboxMessage("Students.StudentLeft")]
    private sealed record UnversionedMessage(Guid Id) : IOutboxMessage;

    [OutboxMessage("tests.probe.v1")]
    private sealed record ProbeDuplicateMessage(Guid Id) : IOutboxMessage;
}
