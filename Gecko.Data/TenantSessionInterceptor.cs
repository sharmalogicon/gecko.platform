using System.Data;
using System.Data.Common;
using Gecko.SharedKernel;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Gecko.Data;

/// <summary>
/// Thrown BEFORE a connection opens when no tenant is known. An unscoped
/// connection would see zero rows today (RLS is fail-closed) — but code that
/// "works" returning empty lists is how a missing scope ships unnoticed.
/// Failing loudly is the point.
/// </summary>
public sealed class MissingTenantContextException()
    : InvalidOperationException("No tenant in the request context. A request with no tenant must not reach a tenant-scoped database.");

/// <summary>
/// Scopes every connection a tenant DbContext opens to the caller's tenant via
/// <c>dbo.usp_set_tenant_context</c>, which sets SESSION_CONTEXT('TenantId')
/// with @read_only = 1 — nothing later in the request can repoint it.
///
/// WHY PER OPEN, NOT PER REQUEST: EF opens and closes the connection around
/// each query. A pooled connection is reset (sp_reset_connection) before its
/// first command after checkout, which clears SESSION_CONTEXT — so the
/// context must be set again on every open, and a request can never inherit
/// the previous request's tenant off the pool.
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        RequireTenant();
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        RequireTenant();
        return ValueTask.FromResult(result);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateSetContextCommand(connection, RequireTenant());
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateSetContextCommand(connection, RequireTenant());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private Guid RequireTenant() => tenant.TenantId ?? throw new MissingTenantContextException();

    private static DbCommand CreateSetContextCommand(DbConnection connection, Guid tenantId)
    {
        var command = connection.CreateCommand();
        command.CommandText = "dbo.usp_set_tenant_context";
        command.CommandType = CommandType.StoredProcedure;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant_id";
        parameter.DbType = DbType.Guid;
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);

        return command;
    }
}
