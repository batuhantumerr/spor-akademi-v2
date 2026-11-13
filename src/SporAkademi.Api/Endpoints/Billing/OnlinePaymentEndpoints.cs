using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using SporAkademi.Api.Endpoints.Students;
using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;

namespace SporAkademi.Api.Endpoints.Billing;

/// <summary>Starts paying one due online: the enrollment and the period (<c>YYYY-MM</c>); the amount is the remaining due.</summary>
internal sealed record StartOnlinePaymentRequest(Guid EnrollmentId, string Period);

/// <summary>The Fake provider's hosted page: the payer pays (<c>succeed</c>) or the card is declined.</summary>
internal sealed record SandboxCompleteRequest(string Token, bool Succeed);

/// <summary>Where the hosted page sends the browser next (a form <c>POST</c> with the token, like iyzico).</summary>
internal sealed record SandboxCompleteResponse(string CallbackUrl);

/// <summary>Acknowledgement of a provider notification; the outcome is informational.</summary>
internal sealed record NotificationAcknowledgement(string Outcome);

/// <summary>
/// Online dues payment (P2-F1). The School Admin starts an attempt and shares its checkout link (DEC-348); the provider
/// reports back through anonymous, signature-verified or re-queried endpoints whose school comes from the stored
/// attempt only (DEC-344).
/// </summary>
internal static class OnlinePaymentEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    public const string CallbackPath = "/api/v1/payment-callbacks";

    /// <summary>The admin's public result page (DEC-348); it shows only the status from the query string.</summary>
    public const string ResultPage = "/odeme-sonucu";

    /// <summary>Provider webhooks: a provider sends bursts from few addresses, so its bucket is larger (DEC-344).</summary>
    public const string WebhookRateLimitPolicy = "payment-webhooks";

    /// <summary>Browser returns and the sandbox page: one payer at a time.</summary>
    public const string BrowserRateLimitPolicy = "payment-callbacks";

    public const int WebhookPermitsPerMinute = 600;
    public const int BrowserPermitsPerMinute = 60;

    /// <summary>A hosted-checkout webhook is a few hundred bytes; larger bodies are refused before they are read.</summary>
    public const long WebhookMaxBodyBytes = 16_384;

    /// <summary>
    /// Partitioned by client IP and path. Behind a reverse proxy the client address comes from the known proxy's
    /// <c>X-Forwarded-For</c> (<c>ForwardedHeaders</c> section, DEC-507); without it every caller shares the proxy's bucket.
    /// </summary>
    public static IServiceCollection AddPaymentNotificationRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(WebhookRateLimitPolicy, context => Partition(context, WebhookPermitsPerMinute));
            options.AddPolicy(BrowserRateLimitPolicy, context => Partition(context, BrowserPermitsPerMinute));
        });

    private static RateLimitPartition<string> Partition(HttpContext context, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"} {context.Request.Path.Value}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    public static IEndpointRouteBuilder MapOnlinePaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var online = app.MapGroup("/api/v1/online-payments").WithTags("Online payments").RequireRole(Role.SchoolAdmin);
        online.MapGet("/", List).Produces<PageResponse<OnlinePaymentDto>>().ProducesProblems(400);
        online.MapGet("/{id:guid}", Get).Produces<OnlinePaymentDto>().ProducesProblems(404);
        online.MapPost("/", Start)
            .Produces<OnlinePaymentDto>(StatusCodes.Status201Created)
            .Produces<OnlinePaymentDto>()
            .ProducesProblems(400, 404, 409, 422);

        var provider = app.MapGroup("/api/v1").WithTags("Payment provider").AllowAnonymous();
        provider.MapPost("/payment-webhooks", Webhook)
            .RequireRateLimiting(WebhookRateLimitPolicy)
            .WithMetadata(new RequestSizeLimit(WebhookMaxBodyBytes))
            .Produces<NotificationAcknowledgement>()
            .ProducesProblems(400, 401, 404, 422, 503);
        provider.MapPost("/payment-callbacks", Callback)
            .RequireRateLimiting(BrowserRateLimitPolicy)
            .WithMetadata(new RequestSizeLimit(WebhookMaxBodyBytes))
            .Produces(StatusCodes.Status303SeeOther)
            .DisableAntiforgery();
        provider.MapPost("/sandbox-checkout/complete", CompleteSandbox)
            .RequireRateLimiting(BrowserRateLimitPolicy)
            .Produces<SandboxCompleteResponse>()
            .ProducesProblems(400, 404);

        return app;
    }

    private static async Task<IResult> List(
        Guid? enrollmentId,
        string? period,
        OnlinePaymentStatus? status,
        int? page,
        int? pageSize,
        IQueryHandler<ListOnlinePaymentsQuery, PagedResult<OnlinePaymentDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new ListOnlinePaymentsQuery(enrollmentId, period, status, EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<OnlinePaymentDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> Get(Guid id, IQueryHandler<GetOnlinePaymentQuery, OnlinePaymentDto> reader, CancellationToken cancellationToken) =>
        (await reader.Handle(new GetOnlinePaymentQuery(OnlinePaymentId.From(id)), cancellationToken)).OkOrProblem();

    /// <summary>
    /// DEC-341: requires an <c>Idempotency-Key</c>. The first request answers 201; repeating it (same key and body) answers
    /// 200 with <c>Idempotent-Replayed: true</c> and the same attempt; the same key with another body is 422.
    /// </summary>
    private static async Task<IResult> Start(
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        StartOnlinePaymentRequest request,
        OnlineCheckout checkout,
        IQueryHandler<GetOnlinePaymentQuery, OnlinePaymentDto> reader,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var started = await checkout.StartAsync(request.EnrollmentId, request.Period, idempotencyKey, cancellationToken);
        if (started.IsFailure)
        {
            return ProblemResults.Problem(started.Error);
        }

        var payment = await reader.Handle(new GetOnlinePaymentQuery(started.Value.Id), cancellationToken);
        if (started.Value.Replayed)
        {
            context.Response.Headers[ReplayedHeader] = "true";
            return payment.OkOrProblem();
        }

        return TypedResults.Created($"/api/v1/online-payments/{started.Value.Id.Value}", payment.Value);
    }

    /// <summary>
    /// The provider's webhook: signature-verified, then the provider is asked for the result. 2xx stops the provider's
    /// retries (applied, repeated or not final yet); 503 asks for a retry; 401/404/422 are refusals.
    /// </summary>
    private static async Task<IResult> Webhook(HttpRequest request, OnlinePaymentNotifications notifications, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await notifications.HandleWebhookAsync(body, headers, cancellationToken);
        return result.Outcome switch
        {
            NotificationOutcome.Applied or NotificationOutcome.Unchanged or NotificationOutcome.Pending =>
                TypedResults.Ok(new NotificationAcknowledgement(result.Outcome.ToString())),
            NotificationOutcome.InvalidSignature => ProblemResults.Problem(OnlinePaymentErrors.InvalidSignature),
            NotificationOutcome.Rejected => ProblemResults.Problem(OnlinePaymentErrors.ProviderMismatch),
            NotificationOutcome.ProviderUnavailable => ServiceUnavailable(),
            _ => ProblemResults.Problem(OnlinePaymentErrors.NotFound),
        };
    }

    /// <summary>
    /// The payer's browser returns from the hosted checkout (a form <c>POST</c> with <c>token</c>). The provider is asked
    /// for the result and the browser is sent to the result page; nothing the browser sends is trusted beyond the token.
    /// </summary>
    private static async Task<IResult> Callback(HttpRequest request, OnlinePaymentNotifications notifications, CancellationToken cancellationToken)
    {
        var token = request.HasFormContentType ? (await request.ReadFormAsync(cancellationToken))["token"].ToString() : string.Empty;
        var result = await notifications.HandleReturnAsync(token, cancellationToken);
        var status = result.Status?.ToString() ?? (result.Outcome == NotificationOutcome.ProviderUnavailable ? nameof(OnlinePaymentStatus.Pending) : "Unknown");
        return new SeeOther($"{ResultPage}?durum={status}");
    }

    /// <summary>DEC-347: the Fake provider's hosted page completes the checkout and sends its webhook, then the browser returns.</summary>
    private static async Task<IResult> CompleteSandbox(
        SandboxCompleteRequest request,
        ISandboxCheckout sandbox,
        OnlinePaymentNotifications notifications,
        CancellationToken cancellationToken)
    {
        if (!sandbox.IsAvailable || string.IsNullOrWhiteSpace(request.Token) || !sandbox.Complete(request.Token, request.Succeed))
        {
            return ProblemResults.Problem(OnlinePaymentErrors.NotFound);
        }

        // Like a real provider, the webhook is sent independently of the browser's return; both are applied once.
        if (sandbox.Webhook(request.Token) is { } webhook)
        {
            await notifications.HandleWebhookAsync(webhook.Body, webhook.Headers, cancellationToken);
        }

        return TypedResults.Ok(new SandboxCompleteResponse(CallbackPath));
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult ServiceUnavailable()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Type = "https://httpstatuses.io/503",
        };
        problem.Extensions["code"] = OnlinePaymentErrors.ProviderUnavailable.Code;
        return TypedResults.Problem(problem);
    }

    /// <summary>303: the browser follows with a GET, whatever method it used.</summary>
    private sealed class SeeOther(string url) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = url;
            return Task.CompletedTask;
        }
    }
}
