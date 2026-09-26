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
