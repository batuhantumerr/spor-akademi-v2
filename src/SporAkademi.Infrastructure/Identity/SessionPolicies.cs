using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>
/// Session lifetimes per role (P5-02, DEC-610). The panel roles (Platform Admin, School Admin, Coach) get an idle timeout
/// and a short absolute lifetime (ASVS L2 3.3.2); the Parent role keeps the long mobile-friendly session of NFR-SEC-05.
/// </summary>
internal sealed class SessionOptions
{
    public const string Section = "Sessions";

    /// <summary>The smallest idle timeout: shorter than the access token's 15 minutes would sign people out while they work.</summary>
    public static readonly TimeSpan MinimumIdleTimeout = TimeSpan.FromMinutes(5);

    public RoleSessionOptions Panel { get; set; } = new() { IdleTimeout = SessionPolicy.Panel.Sliding, AbsoluteLifetime = SessionPolicy.Panel.Absolute };

    public RoleSessionOptions Parent { get; set; } = new() { IdleTimeout = SessionPolicy.Standard.Sliding, AbsoluteLifetime = SessionPolicy.Standard.Absolute };

    public static bool IsValid(RoleSessionOptions options) =>
        options.IdleTimeout >= MinimumIdleTimeout && options.AbsoluteLifetime >= options.IdleTimeout;
}

/// <summary><see cref="IdleTimeout"/>: the longest gap between two refreshes; <see cref="AbsoluteLifetime"/>: the longest a login lasts.</summary>
internal sealed class RoleSessionOptions
{
    public TimeSpan IdleTimeout { get; set; }

    public TimeSpan AbsoluteLifetime { get; set; }

    public SessionPolicy ToPolicy() => new(IdleTimeout, AbsoluteLifetime);
}

internal sealed class SessionPolicies(IOptions<SessionOptions> options) : ISessionPolicies
{
    public SessionPolicy ForRole(UserRole role) =>
        role == UserRole.Parent ? options.Value.Parent.ToPolicy() : options.Value.Panel.ToPolicy();
}

internal static class SessionPoliciesRegistration
{
    public static IServiceCollection AddSessionPolicies(this IServiceCollection services)
    {
        services.AddOptions<SessionOptions>()
            .BindConfiguration(SessionOptions.Section)
            .Validate(o => SessionOptions.IsValid(o.Panel), "Sessions:Panel needs IdleTimeout of at least 5 minutes and AbsoluteLifetime not shorter than it.")
            .Validate(o => SessionOptions.IsValid(o.Parent), "Sessions:Parent needs IdleTimeout of at least 5 minutes and AbsoluteLifetime not shorter than it.")
            .ValidateOnStart();
        services.AddSingleton<ISessionPolicies, SessionPolicies>();
        return services;
    }
}
