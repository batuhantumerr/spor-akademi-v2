using NetArchTest.Rules;

namespace SporAkademi.ArchitectureTests;

/// <summary>
/// P2-F4 (DEC-350): e-mail leaves only after commit, from outbox handlers, never inside a request; MailKit is used in one
/// place, <c>SporAkademi.Infrastructure.Notifications</c>.
/// </summary>
[Trait("Story", "P2-F4")]
public sealed class NotificationRulesTests
{
    private const string EmailSender = "SporAkademi.Application.Abstractions.Notifications.IEmailSender";

    /// <summary>The only application types that may send: outbox message handlers and the mailer they share.</summary>
    private static readonly string[] Senders =
    [
        "SporAkademi.Application.Billing.SendDuesReminderEmail",
        "SporAkademi.Application.Identity.Passwords.PasswordLinkMailer",
    ];

    [Fact]
    public void Only_outbox_handlers_send_e_mail()
    {
        var result = Types.InAssembly(Assemblies.Application)
            .That().HaveDependencyOn(EmailSender)
            .And().DoNotResideInNamespace("SporAkademi.Application.Abstractions.Notifications")
            .GetTypes()
            .Select(t => t.FullName!)
            .ToList();

        result.ShouldBe(Senders, ignoreOrder: true);
        Assemblies.Application.GetType(Senders[0])!.GetInterfaces()
            .ShouldContain(i => i.IsGenericType && i.GetGenericTypeDefinition().Name == "IOutboxMessageHandler`1");
    }

    [Fact]
    public void Request_handlers_and_domain_event_consumers_never_send_e_mail()
    {
        var handlerInterfaces = new[] { "ICommandHandler`2", "IQueryHandler`2", "IDomainEventConsumer`1" };
        var offenders = Assemblies.Application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i => handlerInterfaces.Contains(i.Name)))
            .Where(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => p.ParameterType.FullName == EmailSender
                || p.ParameterType.FullName == Senders[1]))
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Only_the_notifications_namespace_uses_mailkit()
    {
        foreach (var assembly in new[] { Assemblies.Application, Assemblies.Infrastructure, Assemblies.Api })
        {
            var result = Types.InAssembly(assembly)
                .That().DoNotResideInNamespace("SporAkademi.Infrastructure.Notifications")
                .ShouldNot().HaveDependencyOnAny("MailKit", "MimeKit")
                .GetResult();

            result.FailingTypeNames.ShouldBeNull();
        }
    }
}
