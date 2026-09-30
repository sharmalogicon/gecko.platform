using Gecko.Data;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Tests;

/// <summary>
/// The local dev gecko_revenue, through the REAL gecko_app login (so RLS and the
/// DENYs are what is tested), plus a sysadmin door used only to clean up rows
/// the API deliberately cannot remove — an APPROVED tariff is immutable.
/// </summary>
internal static class TestDatabase
{
    private const string Server = @"DESKTOP-6AQI384\APPIFY";

    public static readonly string AppConnection =
        Environment.GetEnvironmentVariable("GECKO_REVENUE_APP")
        ?? $"Server={Server};Database=gecko_revenue;User Id=gecko_app;Password=GeckoApp#Dev2026!;TrustServerCertificate=true";

    private static readonly string AdminConnection =
        Environment.GetEnvironmentVariable("GECKO_REVENUE_ADMIN")
        ?? $"Server={Server};Database=gecko_revenue;Integrated Security=true;TrustServerCertificate=true";

    // Fixture tenants — the GUIDs gecko_identity provisioned.
    public static readonly Guid Sct = Guid.Parse("C458E785-33A5-F111-9B0D-00919E4766D5");
    // SSS is the second tenant the tests use. KORAKIT is a real customer (its tariffs
    // come from the Vector migration) — tests must never write into it.
    public static readonly Guid Sss = Guid.Parse("7259E785-33A5-F111-9B0D-00919E4766D5");

    public static RevenueDbContext ForTenant(Guid? tenantId)
    {
        var caller = new ExplicitTenantContext(tenantId, UserId: null);
        var options = new DbContextOptionsBuilder<RevenueDbContext>()
            .UseSqlServer(AppConnection)
            .AddInterceptors(new TenantSessionInterceptor(caller), new AuditStampInterceptor(caller, TimeProvider.System))
            .Options;
        return new RevenueDbContext(options);
    }

    /// <summary>
    /// Synthetic TOS message ids live far above anything the TOS outbox has issued,
    /// so a test can hand a handler a message without colliding with a real one.
    /// </summary>
    public const long SyntheticMessageIdFloor = 9_000_000_000_000;

    /// <summary>
    /// Removes what the reefer tests fed the projections: ZZTU boxes' sessions and
    /// stays, and the inbox rows of the synthetic messages. Projection rows have no
    /// soft delete (gecko_app may not DELETE), so this is the sysadmin door.
    /// </summary>
    public static async Task RemoveReeferTestRowsAsync()
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.inbox WHERE source_context = 'TOS' AND message_id >= @floor;
            DELETE FROM projection.reefer_session WHERE container_no LIKE 'ZZTU%';
            DELETE FROM projection.container_stay WHERE container_no LIKE 'ZZTU%';
            """;
        command.Parameters.AddWithValue("@floor", SyntheticMessageIdFloor);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes the charge lines a test wrote straight into billing.charge (order
    /// numbers starting ZZC-). Charges have no soft delete and gecko_app may not
    /// DELETE, so this is the sysadmin door.
    /// </summary>
    public static async Task RemoveChargeTestRowsAsync(string orderNo)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM billing.charge WHERE order_no = @no AND order_no LIKE 'ZZC-%';
            """;
        command.Parameters.AddWithValue("@no", orderNo);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Soft-deletes a test tariff and everything under it, whatever its status.
    /// Test-only: production has no way to remove an approved price, by design.
    /// </summary>
    public static async Task RemoveTariffAsync(string scheduleNo)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DECLARE @now DATETIMEOFFSET(7) = SYSUTCDATETIME() AT TIME ZONE 'UTC';
            DECLARE @s TABLE (id UNIQUEIDENTIFIER);
            INSERT @s SELECT schedule_id FROM tariff.schedule WHERE schedule_no = @no AND deleted_at IS NULL;
            DECLARE @r TABLE (id UNIQUEIDENTIFIER);
            INSERT @r SELECT tos_rate_id FROM tariff.tos_rate WHERE schedule_id IN (SELECT id FROM @s) AND deleted_at IS NULL;
            UPDATE tariff.rate_condition_value SET deleted_at = @now WHERE deleted_at IS NULL
              AND rate_condition_id IN (SELECT rate_condition_id FROM tariff.rate_condition WHERE owner_id IN (SELECT id FROM @r));
            UPDATE tariff.rate_condition SET deleted_at = @now WHERE deleted_at IS NULL AND owner_id IN (SELECT id FROM @r);
            UPDATE tariff.rate_tier      SET deleted_at = @now WHERE deleted_at IS NULL AND owner_id IN (SELECT id FROM @r);
            UPDATE tariff.tos_rate       SET deleted_at = @now WHERE deleted_at IS NULL AND tos_rate_id IN (SELECT id FROM @r);
            UPDATE tariff.free_time_rule SET deleted_at = @now WHERE deleted_at IS NULL AND schedule_id IN (SELECT id FROM @s);
            UPDATE import.import_row_issue SET deleted_at = @now WHERE deleted_at IS NULL AND import_row_id IN
              (SELECT import_row_id FROM import.import_row WHERE import_batch_id IN (SELECT import_batch_id FROM import.import_batch WHERE schedule_id IN (SELECT id FROM @s)));
            UPDATE import.import_row     SET deleted_at = @now WHERE deleted_at IS NULL AND import_batch_id IN (SELECT import_batch_id FROM import.import_batch WHERE schedule_id IN (SELECT id FROM @s));
            UPDATE import.import_batch   SET deleted_at = @now WHERE deleted_at IS NULL AND schedule_id IN (SELECT id FROM @s);
            UPDATE import.template_export SET deleted_at = @now WHERE deleted_at IS NULL AND schedule_id IN (SELECT id FROM @s);
            UPDATE tariff.schedule       SET deleted_at = @now WHERE deleted_at IS NULL AND schedule_id IN (SELECT id FROM @s);
            """;
        command.Parameters.AddWithValue("@no", scheduleNo);
        await command.ExecuteNonQueryAsync();
    }
}
