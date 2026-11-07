using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Billing;

namespace SporAkademi.Infrastructure.Billing;

/// <summary>
/// Configuration section <c>Payments</c> (DEC-340). Everything is optional: without it the Fake provider is used only in
/// Development and Testing; every other environment (Staging, Production, ...) has online payment disabled unless a
/// provider is configured (Staging may opt into Fake explicitly). No key is read from <c>.env</c>; iyzico keys come from the
/// environment (<c>Payments__Iyzico__ApiKey</c>, <c>Payments__Iyzico__SecretKey</c>) or user secrets.
/// </summary>
internal sealed class PaymentOptions
{
    public const string Section = "Payments";
    public const string FakeName = "Fake";
    public const string IyzicoName = "Iyzico";
    public const string NoneName = "None";

    /// <summary><c>Fake</c>, <c>Iyzico</c> or <c>None</c>; empty picks iyzico when its keys are set, otherwise Fake in Development and Testing only.</summary>
    public string? Provider { get; set; }

    /// <summary>How long a checkout stays payable (and an attempt without a checkout blocks a new one).</summary>
    public TimeSpan CheckoutLifetime { get; set; } = TimeSpan.FromMinutes(30);

    public FakeProviderOptions Fake { get; set; } = new();

    public IyzicoOptions Iyzico { get; set; } = new();

    internal bool IyzicoConfigured => !string.IsNullOrWhiteSpace(Iyzico.ApiKey) && !string.IsNullOrWhiteSpace(Iyzico.SecretKey);

    /// <summary>Open attempts older than this are re-queried at the provider by the reconciliation job.</summary>
    public TimeSpan ReconcileAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Environments where the Fake provider is the default (DEC-340); elsewhere it must be chosen explicitly.</summary>
    internal static bool FakeByDefault(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    /// <summary>The provider in effect, or <c>null</c> for disabled.</summary>
    internal string? Resolve(IHostEnvironment environment) => Provider?.Trim() switch
    {
        null or "" => IyzicoConfigured ? IyzicoName : FakeByDefault(environment) ? FakeName : null,
        var name when string.Equals(name, FakeName, StringComparison.OrdinalIgnoreCase) => FakeName,
        var name when string.Equals(name, IyzicoName, StringComparison.OrdinalIgnoreCase) => IyzicoName,
        var name when string.Equals(name, NoneName, StringComparison.OrdinalIgnoreCase) => null,
        _ => throw new InvalidOperationException("Payments:Provider must be Fake, Iyzico or None."),
    };

    internal string? Validate(IHostEnvironment environment)
    {
        if (CheckoutLifetime <= TimeSpan.Zero || CheckoutLifetime > TimeSpan.FromDays(1))
        {
            return "Payments:CheckoutLifetime must be between zero and one day.";
        }

        if (ReconcileAfter <= TimeSpan.Zero || ReconcileAfter > TimeSpan.FromDays(1))
        {
            return "Payments:ReconcileAfter must be between zero and one day.";
        }

        var production = environment.IsProduction();
        string? active;
        try
        {
            active = Resolve(environment);
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }

        if (active == FakeName && production)
        {
            return "The Fake payment provider cannot run in Production.";
        }

        if (active == FakeName && string.IsNullOrWhiteSpace(Fake.SigningKey))
        {
            return "Payments:Fake:SigningKey must not be empty.";
        }

        if (active != IyzicoName)
        {
            return null;
        }

        if (!IyzicoConfigured)
        {
            return "Payments:Iyzico:ApiKey and Payments:Iyzico:SecretKey are required for the iyzico provider.";
        }

        // DEC-345: P2-F1 is sandbox only; live payments need the Deploy block and the owner's approval.
        if (!Uri.TryCreate(Iyzico.BaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps
            || !string.Equals(baseUrl.Host, IyzicoOptions.SandboxHost, StringComparison.OrdinalIgnoreCase))
        {
            return $"Payments:Iyzico:BaseUrl must be https://{IyzicoOptions.SandboxHost} (sandbox only).";
        }

        if (!Uri.TryCreate(Iyzico.CallbackUrl, UriKind.Absolute, out var callback) || callback.Scheme is not ("https" or "http"))
        {
            return "Payments:Iyzico:CallbackUrl must be the absolute URL of /api/v1/payment-callbacks.";
        }

        return null;
    }
}

internal sealed class FakeProviderOptions
{
    /// <summary>Signs the Fake provider's webhooks. Not a secret: the Fake provider never runs in Production.</summary>
    public string SigningKey { get; set; } = "spor-akademi-fake-sandbox";
}

internal sealed class IyzicoOptions
{
    public const string SandboxHost = "sandbox-api.iyzipay.com";

    public string BaseUrl { get; set; } = $"https://{SandboxHost}";

    public string? ApiKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Absolute URL iyzico sends the payer's browser back to, e.g. <c>https://app.example.test/api/v1/payment-callbacks</c>.</summary>
    public string? CallbackUrl { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Picks the provider once from the options and the environment (DEC-340).</summary>
internal sealed class PaymentProviders(IOptions<PaymentOptions> options, IHostEnvironment environment, IServiceProvider services) : IPaymentProviders
{
    private readonly Lazy<IPaymentProvider?> _active = new(() =>
        options.Value.Resolve(environment) switch
        {
            PaymentOptions.FakeName => services.GetRequiredService<FakePaymentProvider>(),
            PaymentOptions.IyzicoName => services.GetRequiredService<IyzicoPaymentProvider>(),
            _ => null,
        });

    public IPaymentProvider? Active => _active.Value;

    public TimeSpan CheckoutLifetime => options.Value.CheckoutLifetime;
}

/// <summary>The Fake provider's hosted checkout, only while it is the active provider.</summary>
internal sealed class SandboxCheckout(IPaymentProviders providers, FakePaymentProvider fake) : ISandboxCheckout
{
    public bool IsAvailable => ReferenceEquals(providers.Active, fake);

    public bool Complete(string token, bool succeed) => IsAvailable && fake.Complete(token, succeed);

    public (string Body, IReadOnlyDictionary<string, string> Headers)? Webhook(string token) => IsAvailable ? fake.Webhook(token) : null;
}

/// <summary>Fails the start with the exact reason (no secret values in it).</summary>
internal sealed class PaymentOptionsValidator(IHostEnvironment environment) : IValidateOptions<PaymentOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Validate(environment) is { } failure ? ValidateOptionsResult.Fail(failure) : ValidateOptionsResult.Success;
    }
}

internal static class PaymentProviderRegistration
{
    public static IServiceCollection AddPaymentProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PaymentOptions>()
            .Bind(configuration.GetSection(PaymentOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PaymentOptions>, PaymentOptionsValidator>();

        services.AddSingleton<IPaymentProviders, PaymentProviders>();
        services.AddSingleton<FakePaymentProvider>();
        services.AddSingleton<ISandboxCheckout, SandboxCheckout>();
        services.AddHttpClient(IyzicoPaymentProvider.HttpClientName);
        services.AddSingleton<IyzicoPaymentProvider>();
        services.AddScoped<ICheckoutTenantResolver, Tenancy.CheckoutTenantResolver>();
        services.AddScoped<ITenantWorkScope, Persistence.TenantWorkScope>();
        services.AddScoped<IOnlinePaymentRepository, OnlinePaymentRepository>();
        return services;
    }
}
