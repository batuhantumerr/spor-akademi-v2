using Microsoft.Extensions.DependencyInjection;

namespace SporAkademi.Infrastructure.Messaging;

public static class ConsumerHosting
{
    /// <summary>
    /// P5-04 (DEC-630): fixes this process's broker consumers to one group (see
    /// <see cref="SporAkademi.Application.Abstractions.Outbox.OutboxConsumerGroupAttribute"/>), whatever the configuration says:
    /// consumers on, all modules, no excluded group. A dedicated worker uses it so a deployment's settings cannot make it
    /// consume (and so swallow) messages whose handlers belong to another process. Call it after <c>AddInfrastructure</c>.
    /// </summary>
    public static IServiceCollection RestrictConsumersToGroup(this IServiceCollection services, string group)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        services.PostConfigure<RabbitMqOptions>(options =>
        {
            options.Consumers.Enabled = true;
            options.Consumers.Modules = [];
            options.Consumers.Groups = [group];
            options.Consumers.ExcludeGroups = [];
        });
        return services;
    }
}
