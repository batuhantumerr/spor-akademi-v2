using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SporAkademi.Application.Abstractions.Authentication;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// Layer 3 of ADR-0003 (P4-04, DEC-520): every time EF opens a connection, the session is told whose rows it may touch,
/// and PostgreSQL's row level security policies enforce it even where the query filters are bypassed (raw SQL, or a
/// query that ignores the filters). Both settings are written on every open, at session level, so a pooled connection never
/// keeps the previous user's school: the value always comes from this scope's <see cref="ICurrentUser"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>app.tenant_id</c>: the school of the scope, or empty. Tenant tables only show and accept its rows.</item>
/// <item><c>app.platform</c>: <c>on</c> only for deliberate work without a school (anonymous calls such as login and the
/// payment webhook, the Platform Admin, platform-level background work). It opens the platform tables (schools, users,
/// outbox messages; DEC-105, DEC-302) and nothing else. A signed-in school user without a school gets neither, so it
/// sees nothing.</item>
/// </list>
/// A request scope marked with <see cref="PlatformDataScope"/> (anonymous identity and payment provider endpoints) is
/// platform-level whatever token it carries (DEC-526).
/// </remarks>
internal sealed class TenantSessionInterceptor(ICurrentUser currentUser, PlatformDataScope platformScope) : DbConnectionInterceptor
{
    public const string TenantSetting = "app.tenant_id";
    public const string PlatformSetting = "app.platform";

    private const string Sql = $"SELECT set_config('{TenantSetting}', @tenant, false), set_config('{PlatformSetting}', @platform, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var tenant = platformScope.IsEntered ? null : currentUser.TenantId;
        var platform = platformScope.IsEntered
            || (tenant is null && (!currentUser.IsAuthenticated || currentUser.Role == Role.PlatformAdmin));

        var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", tenant?.ToString() ?? string.Empty));
        command.Parameters.Add(new NpgsqlParameter<string>("platform", platform ? "on" : string.Empty));
        return command;
    }
}
