using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Net.Http.Headers;
using SporAkademi.Api.Endpoints.Tenancy;
using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Reports;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Api.Endpoints.Reports;

/// <summary>
/// The School Admin reports (P3-09, DEC-406 – DEC-411): attendance, occupancy and revenue as JSON for the page, and each
/// as a CSV download. School Admin only; the exports have their own per-account rate limit.
/// </summary>
internal static class ReportEndpoints
{
    /// <summary>DEC-411: CSV exports per School Admin account (the token's subject) and minute.</summary>
    public const string ExportRateLimitPolicy = "report-exports";
    public const int ExportsPerMinute = 10;

    public const string CsvContentType = "text/csv; charset=utf-8";

    public static IServiceCollection AddReportExportRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options => options.AddPolicy(ExportRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = ExportsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        // P4-10 (DEC-585): behind the "reports" flag; while it is off for the school every report answers 404.
        var reports = app.MapGroup("/api/v1/reports").WithTags("Reports").RequireRole(Role.SchoolAdmin)
            .RequireFeature(FeatureCatalog.Reports);
        reports.MapGet("/attendance", GetAttendance).Produces<AttendanceReportDto>().ProducesProblems(400, 404);
        reports.MapGet("/occupancy", GetOccupancy).Produces<OccupancyReportDto>().ProducesProblems(400, 404);
        reports.MapGet("/revenue", GetRevenue).Produces<RevenueReportDto>().ProducesProblems(400, 404);

        reports.MapGet("/attendance/csv", ExportAttendance).Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblems(400, 404, 429).RequireRateLimiting(ExportRateLimitPolicy);
        reports.MapGet("/occupancy/csv", ExportOccupancy).Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblems(400, 404, 429).RequireRateLimiting(ExportRateLimitPolicy);
        reports.MapGet("/revenue/csv", ExportRevenue).Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblems(400, 404, 429).RequireRateLimiting(ExportRateLimitPolicy);
        return app;
    }

    private static async Task<IResult> GetAttendance(
        string? from,
        string? to,
        string? by,
        Guid? sportId,
        Guid? coachId,
        IQueryHandler<GetAttendanceReportQuery, AttendanceReportDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetAttendanceReportQuery(from, to, by, sportId, coachId), cancellationToken)).OkOrProblem();

    private static async Task<IResult> GetOccupancy(
        string? from,
        string? to,
        string? by,
        Guid? sportId,
        Guid? coachId,
        IQueryHandler<GetOccupancyReportQuery, OccupancyReportDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetOccupancyReportQuery(from, to, by, sportId, coachId), cancellationToken)).OkOrProblem();

    private static async Task<IResult> GetRevenue(
        string? from,
        string? to,
        IQueryHandler<GetRevenueReportQuery, RevenueReportDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetRevenueReportQuery(from, to), cancellationToken)).OkOrProblem();

    private static async Task<IResult> ExportAttendance(
        string? from,
        string? to,
        string? by,
        Guid? sportId,
        Guid? coachId,
        IQueryHandler<GetAttendanceReportQuery, AttendanceReportDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetAttendanceReportQuery(from, to, by, sportId, coachId), cancellationToken);
        return result.IsSuccess
            ? new CsvResult("attendance", result.Value.From, result.Value.To, csv => csv.Attendance(result.Value))
            : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> ExportOccupancy(
        string? from,
        string? to,
        string? by,
        Guid? sportId,
        Guid? coachId,
        IQueryHandler<GetOccupancyReportQuery, OccupancyReportDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetOccupancyReportQuery(from, to, by, sportId, coachId), cancellationToken);
        return result.IsSuccess
            ? new CsvResult("occupancy", result.Value.From, result.Value.To, csv => csv.Occupancy(result.Value))
            : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> ExportRevenue(
        string? from,
        string? to,
        IQueryHandler<GetRevenueReportQuery, RevenueReportDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetRevenueReportQuery(from, to), cancellationToken);
        return result.IsSuccess
            ? new CsvResult("revenue", result.Value.From, result.Value.To, csv => csv.Revenue(result.Value))
            : ProblemResults.Problem(result.Error);
    }

    /// <summary>
    /// Streams the rows to the response as UTF-8 with a byte order mark (Excel then reads Turkish letters correctly);
    /// the rows are produced and written one by one, the CSV text is never built in memory as a whole.
    /// </summary>
    private sealed class CsvResult(string report, string from, string to, Func<ReportCsv, IEnumerable<IReadOnlyList<string>>> rows) : IResult
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            // DEC-413: headers, separator and number format follow Accept-Language like the problem texts (DEC-430).
            var csv = ReportCsv.For(ProblemLanguage.From(httpContext));
            var response = httpContext.Response;
            response.ContentType = CsvContentType;
            response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = csv.FileName(report, from, to) }.ToString();
            response.Headers.XContentTypeOptions = "nosniff";

            await response.Body.WriteAsync(Encoding.UTF8.Preamble.ToArray(), httpContext.RequestAborted);
            await using var writer = new StreamWriter(response.Body, Utf8, bufferSize: 16 * 1024, leaveOpen: true);
            await csv.WriteAsync(writer, rows(csv), httpContext.RequestAborted);
        }
    }
}
