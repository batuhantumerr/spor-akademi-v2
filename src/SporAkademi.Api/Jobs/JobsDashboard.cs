using System.Security.Claims;
using System.Text.Encodings.Web;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SporAkademi.Api.Endpoints;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Infrastructure.Jobs;

namespace SporAkademi.Api.Jobs;

/// <summary>
/// The job panel (Hangfire Dashboard at <c>/jobs</c>, DEC-332). It shows every school's jobs, so only an active
/// Platform Admin may open it. The admin app keeps its access token in memory, which a browser navigation to the
/// panel cannot carry, so the app first calls <c>POST /api/v1/platform/jobs/dashboard-session</c> with its token;
/// the API answers with a short-lived, HttpOnly, Secure, SameSite=Strict cookie scoped to <c>/jobs</c>. The panel
/// accepts nothing else (no bearer token, no anonymous access), re-checks the user on every request, and validates
/// an antiforgery token on its POST actions (which are off unless <c>Jobs:Dashboard:ReadOnly</c> is false).
/// </summary>
internal static class JobsDashboard
{
    public const string Scheme = "JobsDashboard";
    public const string Policy = "JobsDashboard";
    public const string AntiforgeryCookieName = "sa_jobs_af";

    public static IServiceCollection AddJobsDashboard(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, JobsDashboardAuthenticationHandler>(Scheme, null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()
            .RequireRole(nameof(Role.PlatformAdmin)));

        // Hangfire uses ASP.NET Core antiforgery for the panel's POST actions when it is registered.
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.Path = JobsDashboardSessions.Path;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.HeaderName = "X-Jobs-Antiforgery";
        });
        return services;
    }

    public static WebApplication MapJobsDashboard(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/v1/platform/jobs").WithTags("Jobs").RequireRole(Role.PlatformAdmin);
        jobs.MapPost("/dashboard-session", StartSession).Produces(StatusCodes.Status204NoContent).ProducesProblems();

        var settings = app.Services.GetRequiredService<IOptions<JobsOptions>>();
        app.MapHangfireDashboard(JobsDashboardSessions.Path, new DashboardOptions
        {
            Authorization = [new PlatformAdminOnly()],
            IsReadOnlyFunc = _ => settings.Value.Dashboard.ReadOnly,
            AppPath = settings.Value.Dashboard.AppPath,
            DashboardTitle = "Spor Akademi — İşler",
            DisplayStorageConnectionString = false,
            StatsPollingInterval = 5_000,
        })
            .RequireAuthorization(Policy)
            .ExcludeFromDescription();
        return app;
    }

    /// <summary>Starts a panel session for the calling Platform Admin; a previous session of theirs ends.</summary>
    private static async Task<IResult> StartSession(
        HttpContext context,
        ICurrentUser user,
        IJobsDashboardAccess access,
        JobsDashboardSessions sessions,
        IOptions<JobsOptions> options,
        CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId || !await access.IsActivePlatformAdminAsync(userId, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var session = sessions.Create(userId);
        context.Response.Cookies.Append(JobsDashboardSessions.CookieName, session.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true, // browsers also keep Secure cookies from http://localhost, so local development works
            SameSite = SameSiteMode.Strict,
            Path = JobsDashboardSessions.Path,
            MaxAge = options.Value.Dashboard.SessionLifetime,
            IsEssential = true,
        });
        return TypedResults.NoContent();
    }

    /// <summary>Defense in depth behind the endpoint policy: Hangfire itself also requires the panel's Platform Admin.</summary>
    private sealed class PlatformAdminOnly : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var user = context.GetHttpContext().User;
            return user.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == Scheme)
                && user.IsInRole(nameof(Role.PlatformAdmin));
        }
    }
}

/// <summary>Authenticates panel requests by the session cookie only; the user must still be an active Platform Admin.</summary>
internal sealed class JobsDashboardAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    JobsDashboardSessions sessions,
    IJobsDashboardAccess access) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(JobsDashboardSessions.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        if (sessions.Find(token) is not { } userId)
        {
            return AuthenticateResult.Fail("The job panel session is unknown or expired.");
        }

        if (!await access.IsActivePlatformAdminAsync(userId, Context.RequestAborted))
        {
            sessions.End(userId);
            return AuthenticateResult.Fail("The job panel session's user may no longer use it.");
        }

        var identity = new ClaimsIdentity(
            [new Claim("sub", userId.ToString()), new Claim("role", nameof(Role.PlatformAdmin))],
            JobsDashboard.Scheme,
            "sub",
            "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), JobsDashboard.Scheme));
    }
}
