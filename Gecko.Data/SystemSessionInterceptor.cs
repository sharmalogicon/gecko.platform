using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Gecko.Data;

/// <summary>
/// Raises SESSION_CONTEXT('IsSystemContext') = 1 on every open — cross-tenant
/// visibility for login, invitation acceptance, password reset and provisioning.
///
/// SAFE ONLY ON THE gecko_system CONNECTION STRING. The RLS predicate honours
/// the flag only when the login is a member of the gecko_system role, so on
/// gecko_app it does nothing. And because SqlClient pools per connection
/// string, a connection carrying the flag is only ever handed back to another
/// system-context caller; sp_reset_connection clears it on checkout anyway.
/// </summary>
public sealed class SystemSessionInterceptor : DbConnectionInterceptor
{
    private const string Sql = "EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
