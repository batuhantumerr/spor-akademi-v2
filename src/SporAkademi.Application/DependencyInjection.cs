using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Contracts;

namespace SporAkademi.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AccountOpening>();
        services.AddScoped<IIdentityModule, IdentityModule>();
        services.AddScoped<EnrollmentModule>();
        services.AddScoped<IEnrollmentCounts>(sp => sp.GetRequiredService<EnrollmentModule>());
        services.AddScoped<IEnrollmentEnder>(sp => sp.GetRequiredService<EnrollmentModule>());
        services.AddScoped<IStudentGroupFilter>(sp => sp.GetRequiredService<EnrollmentModule>());
        return services.AddHandlersFrom(typeof(DependencyInjection).Assembly);
    }

    /// <summary>
    /// Registers handlers and validators from <paramref name="assembly"/> and wraps them in decorators.
    /// Scrutor applies decorators inside-out, so the last Decorate call is the outermost.
    /// </summary>
    internal static IServiceCollection AddHandlersFrom(this IServiceCollection services, Assembly assembly)
    {
        services.Scan(scan => scan.FromAssemblies(assembly)
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IDomainEventConsumer<>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime());

        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Singleton, includeInternalTypes: true);

        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TransactionCommandDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationCommandDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingCommandDecorator<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(ValidationQueryDecorator<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingQueryDecorator<,>));

        return services;
    }
}
