using SporAkademi.Application.Abstractions.Auditing;

namespace SporAkademi.Api.Endpoints;

/// <summary>
/// P4-05 (DEC-532): which reads of personal data are recorded, declared on the endpoint. An entry is written only when
/// the endpoint answers with success. A change answered with the changed record (<c>ThenRead</c>) is not also a read:
/// the record is read inside the change's endpoint, which carries no read filter.
/// </summary>
internal static class AuditedReads
{
    /// <summary>One record, named by the <c>id</c> route value: a detail page, a document link, an export.</summary>
    public static TBuilder AuditsRead<TBuilder>(this TBuilder builder, AuditEntityType type, AuditAction action = AuditAction.Read, params string[] fields)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (IsSuccess(result) && Guid.TryParse(context.HttpContext.GetRouteValue("id")?.ToString(), out var id))
            {
                await RecordAsync(context.HttpContext, new AuditRecord(action, type, id, fields));
            }

            return result;
        });

    /// <summary>
    /// A list or search: one entry for the query, however many rows it returns. Its fields are the names of the filters
    /// used, taken only from <paramref name="parameters"/>: query string keys are client input, and values are never kept.
    /// </summary>
    public static TBuilder AuditsList<TBuilder>(this TBuilder builder, AuditEntityType type, params string[] parameters)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (IsSuccess(result))
            {
                var query = context.HttpContext.Request.Query;
                var used = parameters.Where(p => query.TryGetValue(p, out var value) && !string.IsNullOrEmpty(value)).ToList();
                await RecordAsync(context.HttpContext, new AuditRecord(AuditAction.List, type, null, used));
            }

            return result;
        });

    private static bool IsSuccess(object? result) =>
        result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 };

    private static Task RecordAsync(HttpContext context, AuditRecord record) =>
        context.RequestServices.GetRequiredService<IAuditTrail>().RecordAsync(record, context.RequestAborted);
}
