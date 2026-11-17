using System.Reflection;
using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.ArchitectureTests;

/// <summary>P5-04 (DEC-630 – DEC-632): the notifications worker's references and which handlers it owns.</summary>
[Trait("Story", "P5-04")]
public sealed class NotificationsWorkerRulesTests
{
    private static readonly string[] AllowedReferences = ["SporAkademi.Application", "SporAkademi.Infrastructure", "SporAkademi.Domain"];

    [Fact]
    public void The_worker_references_only_the_application_infrastructure_and_domain_layers()
    {
        var references = Assemblies.Notifications.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("SporAkademi.", StringComparison.Ordinal))
            .ToList();

        references.ShouldBeSubsetOf(AllowedReferences);
        references.ShouldContain("SporAkademi.Application");
        references.ShouldContain("SporAkademi.Infrastructure");
    }

    [Fact]
    public void No_layer_depends_on_the_worker()
    {
        foreach (var assembly in new[] { Assemblies.Domain, Assemblies.Application, Assemblies.Infrastructure, Assemblies.Api })
        {
            assembly.GetReferencedAssemblies().Select(a => a.Name).ShouldNotContain("SporAkademi.Notifications");
        }
    }

    [Fact]
    public void The_worker_and_the_api_build_outputs_do_not_contain_each_other()
    {
        var apiDeps = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assemblies.Api.Location)!, "SporAkademi.Api.deps.json"));
        var workerDeps = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assemblies.Notifications.Location)!, "SporAkademi.Notifications.deps.json"));

        apiDeps.ShouldNotContain("SporAkademi.Notifications");
        workerDeps.ShouldNotContain("SporAkademi.Api");
        workerDeps.ShouldNotContain("SporAkademi.DemoSeed");
    }

    /// <summary>
    /// DEC-634: the worker never keeps its own copy of what leaves the process for OTLP or Sentry; the scrubbers live in
    /// Infrastructure and both hosts use those classes, so the worker cannot be weaker than the API.
    /// </summary>
    [Fact]
    public void The_worker_has_no_scrubbing_of_its_own()
    {
        var own = Assemblies.Notifications.GetTypes()
            .Where(t => t.Name.Contains("Scrub", StringComparison.Ordinal) || t.Name.Contains("Redact", StringComparison.Ordinal)
                || typeof(OpenTelemetry.BaseProcessor<System.Diagnostics.Activity>).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();
        own.ShouldBeEmpty();

        var shared = Assemblies.Infrastructure.GetType("SporAkademi.Infrastructure.Observability.TelemetryScrubber");
        shared.ShouldNotBeNull();
        Assemblies.Api.GetTypes().Where(t => t.Name == "TelemetryScrubber").ShouldBeEmpty();
    }

    /// <summary>
    /// A handler that sends e-mail must be in the <c>notifications</c> group, otherwise the API would keep consuming it while the
    /// worker runs (two hosts, two e-mails). Real-time and other handlers must not be, otherwise the worker would swallow them.
    /// </summary>
    [Fact]
    public void Exactly_the_e_mail_handlers_are_in_the_notifications_group()
    {
        var handlers = Assemblies.Application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IOutboxMessageHandler<>)))
            .ToList();
        handlers.ShouldNotBeEmpty();

        var inGroup = handlers.Where(t => GroupOf(t) == OutboxConsumerGroups.Notifications).Select(t => t.Name).Order().ToList();
        var sendingEmail = handlers.Where(SendsEmail).Select(t => t.Name).Order().ToList();

        inGroup.ShouldBe(sendingEmail);
        inGroup.ShouldNotBeEmpty();
        handlers.Where(t => GroupOf(t) is not null and not OutboxConsumerGroups.Notifications).ShouldBeEmpty();
    }

    private static string? GroupOf(Type type) => type.GetCustomAttribute<OutboxConsumerGroupAttribute>(inherit: false)?.Group;

    /// <summary>The handler takes <c>IEmailSender</c> or the shared mailer, directly.</summary>
    private static bool SendsEmail(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType.Name is "IEmailSender" or "PasswordLinkMailer");
}
