using Gecko.Data;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Tests;

/// <summary>
/// The local dev gecko_tos, through the REAL gecko_app login (so RLS and the
/// DENYs are what is tested), plus a sysadmin door used only to clean up: the
/// API has no delete for a vessel call, by design — a call is cancelled, not erased.
/// </summary>
internal static class TestDatabase
{
    private const string Server = @"DESKTOP-6AQI384\APPIFY";

    public static readonly string AppConnection =
        Environment.GetEnvironmentVariable("GECKO_TOS_APP")
        ?? $"Server={Server};Database=gecko_tos;User Id=gecko_app;Password=GeckoApp#Dev2026!;TrustServerCertificate=true";

    private static readonly string AdminConnection =
        Environment.GetEnvironmentVariable("GECKO_TOS_ADMIN")
        ?? $"Server={Server};Database=gecko_tos;Integrated Security=true;TrustServerCertificate=true";

    // Fixture tenants — the GUIDs gecko_identity provisioned.
    public static readonly Guid Sct = Guid.Parse("C458E785-33A5-F111-9B0D-00919E4766D5");
    public static readonly Guid Sss = Guid.Parse("7259E785-33A5-F111-9B0D-00919E4766D5");

    /// <summary>SCT's Bangkok depot — the fixture gives it its own, earlier yard cut-off.</summary>
    public static readonly Guid SctBkk01 = Guid.Parse("775AE785-33A5-F111-9B0D-00919E4766D5");

    /// <summary>SSS's branch — for SCT, a branch of another tenant. (KORAKIT is a real client now, never a test tenant.)</summary>
    public static readonly Guid SssLcb01 = Guid.Parse("7359E785-33A5-F111-9B0D-00919E4766D5");

    public static TosDbContext ForTenant(Guid? tenantId)
    {
        var caller = new ExplicitTenantContext(tenantId, UserId: null);
        var options = new DbContextOptionsBuilder<TosDbContext>()
            .UseSqlServer(AppConnection)
            .AddInterceptors(new TenantSessionInterceptor(caller), new AuditStampInterceptor(caller, TimeProvider.System))
            .Options;
        return new TosDbContext(options);
    }

    /// <summary>Soft-deletes a test call and its lines and cut-offs. Test-only.</summary>
    public static async Task RemoveCallAsync(string callRef)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DECLARE @now DATETIMEOFFSET(7) = SYSUTCDATETIME() AT TIME ZONE 'UTC';
            DECLARE @c TABLE (id UNIQUEIDENTIFIER);
            INSERT @c SELECT vessel_call_id FROM vessel.vessel_call WHERE call_ref = @ref AND deleted_at IS NULL;
            UPDATE vessel.vessel_call_cutoff SET deleted_at = @now WHERE deleted_at IS NULL AND vessel_call_id IN (SELECT id FROM @c);
            UPDATE vessel.vessel_call_line   SET deleted_at = @now WHERE deleted_at IS NULL AND vessel_call_id IN (SELECT id FROM @c);
            UPDATE vessel.vessel_call        SET deleted_at = @now WHERE deleted_at IS NULL AND vessel_call_id IN (SELECT id FROM @c);
            """;
        command.Parameters.AddWithValue("@ref", callRef);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Soft-deletes test bookings (carrier_ref starting with the prefix) and everything under them. Test-only.</summary>
    public static async Task RemoveBookingsAsync(string carrierRefPrefix)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DECLARE @now DATETIMEOFFSET(7) = SYSUTCDATETIME() AT TIME ZONE 'UTC';
            DECLARE @b TABLE (id UNIQUEIDENTIFIER);
            INSERT @b SELECT booking_id FROM booking.booking WHERE carrier_ref LIKE @prefix + '%' AND deleted_at IS NULL;
            UPDATE booking.movement_plan SET deleted_at = @now WHERE deleted_at IS NULL AND booking_container_id IN
                (SELECT booking_container_id FROM booking.booking_container WHERE booking_id IN (SELECT id FROM @b));
            UPDATE booking.booking_container     SET deleted_at = @now WHERE deleted_at IS NULL AND booking_id IN (SELECT id FROM @b);
            UPDATE booking.equipment_requirement SET deleted_at = @now WHERE deleted_at IS NULL AND booking_id IN (SELECT id FROM @b);
            UPDATE booking.cutoff_exception      SET deleted_at = @now WHERE deleted_at IS NULL AND booking_id IN (SELECT id FROM @b);
            UPDATE yard.container_hold           SET deleted_at = @now WHERE deleted_at IS NULL AND booking_id IN (SELECT id FROM @b);
            UPDATE booking.booking               SET deleted_at = @now WHERE deleted_at IS NULL AND booking_id IN (SELECT id FROM @b);
            DELETE FROM outbox.message WHERE aggregate_type = 'BOOKING' AND aggregate_id IN (SELECT id FROM @b);
            """;
        command.Parameters.AddWithValue("@prefix", carrierRefPrefix);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Soft-deletes test holds (by container number). Test-only — a hold is released, never erased.</summary>
    public static async Task RemoveHoldsAsync(params string[] containerNos)
    {
        if (containerNos.Length == 0) return;

        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            UPDATE yard.container_hold SET deleted_at = SYSUTCDATETIME() AT TIME ZONE 'UTC'
            WHERE deleted_at IS NULL AND container_no IN (SELECT value FROM STRING_SPLIT(@numbers, ','));
            """;
        command.Parameters.AddWithValue("@numbers", string.Join(',', containerNos));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The outbox message a gate event queued. Read through the sysadmin door:
    /// gecko_app is DENIED SELECT on outbox.* by design (11_outbox) — the module
    /// may queue a message and may never read the queue.
    /// </summary>
    public static async Task<(string MessageType, string PayloadJson)> OutboxAsync(Guid aggregateId)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP 1 message_type, payload_json FROM outbox.message WHERE aggregate_id = @id ORDER BY message_id DESC;
            """;
        command.Parameters.AddWithValue("@id", aggregateId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException($"No outbox message for {aggregateId}.");
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>Every outbox message queued for these aggregates, in queue order (message_id).</summary>
    public static async Task<List<(long MessageId, Guid AggregateId, string MessageType, string PayloadJson)>> OutboxInOrderAsync(params Guid[] aggregateIds)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT message_id, aggregate_id, message_type, payload_json FROM outbox.message
            WHERE aggregate_id IN ({string.Join(",", aggregateIds.Select((_, i) => $"@a{i}"))}) ORDER BY message_id;
            """;
        for (var i = 0; i < aggregateIds.Length; i++) command.Parameters.AddWithValue($"@a{i}", aggregateIds[i]);
        var rows = new List<(long, Guid, string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    /// <summary>
    /// Removes the gate rows a test wrote, for bookings carrying the prefix.
    ///
    /// HARD deletes, through the sysadmin door, because that is the only door:
    /// an EIR is append-only by GRANT (10_gate_transaction.sql), so neither
    /// application login can soft-delete one — which is the point.
    /// </summary>
    public static async Task RemoveGateAsync(string carrierRefPrefix)
    {
        // Revenue heard these gate events too (the dispatcher runs in the test host):
        // its stays and booking copies go with them, or the next run finds a stay open.
        await RemoveCashWindowAsync(carrierRefPrefix, null);

        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DECLARE @b TABLE (id UNIQUEIDENTIFIER);
            INSERT @b SELECT booking_id FROM booking.booking WHERE carrier_ref LIKE @prefix + '%';
            DECLARE @g TABLE (id UNIQUEIDENTIFIER);
            INSERT @g SELECT gate_transaction_id FROM gate.gate_transaction WHERE booking_id IN (SELECT id FROM @b);
            DECLARE @v TABLE (id UNIQUEIDENTIFIER);
            INSERT @v SELECT DISTINCT truck_visit_id FROM gate.gate_transaction WHERE gate_transaction_id IN (SELECT id FROM @g);
            DECLARE @cv TABLE (id UNIQUEIDENTIFIER);
            INSERT @cv SELECT container_visit_id FROM yard.container_visit
                WHERE gate_in_transaction_id IN (SELECT id FROM @g) OR gate_out_transaction_id IN (SELECT id FROM @g);

            DELETE FROM gate.survey_damage WHERE survey_id IN (SELECT survey_id FROM gate.survey WHERE container_visit_id IN (SELECT id FROM @cv));
            DELETE FROM gate.survey            WHERE container_visit_id IN (SELECT id FROM @cv);
            DECLARE @rs TABLE (id UNIQUEIDENTIFIER);
            INSERT @rs SELECT reefer_power_session_id FROM yard.reefer_power_session WHERE container_visit_id IN (SELECT id FROM @cv);
            DELETE FROM outbox.message         WHERE aggregate_type = 'REEFER_SESSION' AND aggregate_id IN (SELECT id FROM @rs);
            DELETE FROM yard.reefer_power_session WHERE reefer_power_session_id IN (SELECT id FROM @rs);
            DELETE FROM yard.visit_event       WHERE container_visit_id IN (SELECT id FROM @cv);
            DELETE FROM yard.container_visit   WHERE container_visit_id IN (SELECT id FROM @cv);
            DELETE FROM gate.gate_authorization WHERE booking_id IN (SELECT id FROM @b);
            DELETE FROM gate.attachment WHERE owner_id IN (SELECT id FROM @g) OR owner_id IN (SELECT id FROM @v);
            DELETE FROM gate.gate_transaction_seal WHERE gate_transaction_id IN (SELECT id FROM @g);
            DELETE FROM outbox.message         WHERE aggregate_id IN (SELECT id FROM @g);
            DELETE FROM gate.gate_transaction  WHERE gate_transaction_id IN (SELECT id FROM @g);
            DELETE FROM gate.truck_visit       WHERE truck_visit_id IN (SELECT id FROM @v);
            """;
        command.Parameters.AddWithValue("@prefix", carrierRefPrefix);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Marks a box's first step DONE as if a gate transaction had completed it.
    /// Test-only: the gate is Phase 5, and no API can do this — DONE means "a
    /// gate transaction says so" (D-4), so this invents the transaction id.
    /// </summary>
    public static async Task MarkFirstStepDoneAsync(Guid bookingContainerId)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            UPDATE booking.movement_plan SET status = 'DONE', gate_transaction_id = NEWID()
            WHERE booking_container_id = @box AND deleted_at IS NULL
              AND sequence_no = (SELECT MIN(sequence_no) FROM booking.movement_plan WHERE booking_container_id = @box AND deleted_at IS NULL);
            """;
        command.Parameters.AddWithValue("@box", bookingContainerId);
        await command.ExecuteNonQueryAsync();
    }

    // ── the cash window (Revenue), for CashWindowFlowTests ──────────────────

    /// <summary>
    /// Sets (true/false) or removes (null) a BRANCH override of gate.require_coupon_for_cash
    /// in gecko_master. The fixture tenants warn only (dev_08); the cash-window test
    /// needs the real rule at one branch, for the length of one test.
    /// </summary>
    public static async Task RequireCouponAtAsync(Guid branchId, bool? value)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DELETE FROM gecko_master.config.tenant_setting
             WHERE branch_id = @branch AND setting_key = 'gate.require_coupon_for_cash';
            IF @value IS NOT NULL
                INSERT INTO gecko_master.config.tenant_setting (tenant_id, branch_id, setting_key, setting_value)
                VALUES (@tenant, @branch, 'gate.require_coupon_for_cash', @value);
            """;
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@tenant", Sct);
        command.Parameters.AddWithValue("@value", value is null ? DBNull.Value : value.Value ? "true" : "false");
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes what Revenue wrote for the test's bookings — receipts, charges, the
    /// drawer, the booking copy, the stays, the inbox and the coupons it queued.
    /// HARD deletes through the sysadmin door: a receipt is append-only by GRANT, so
    /// no application login can remove one — which is the point. Test-only, and only
    /// ever for fixture tenants.
    /// </summary>
    public static async Task RemoveCashWindowAsync(string carrierRefPrefix, Guid? openShiftId)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            DECLARE @b TABLE (id UNIQUEIDENTIFIER, tenant_id UNIQUEIDENTIFIER);
            INSERT @b SELECT booking_id, tenant_id FROM booking.booking WHERE carrier_ref LIKE @prefix + '%';
            DECLARE @boxes TABLE (id UNIQUEIDENTIFIER, container_no VARCHAR(11));
            INSERT @boxes SELECT booking_container_id, container_no FROM booking.booking_container WHERE booking_id IN (SELECT id FROM @b);
            DECLARE @r TABLE (id UNIQUEIDENTIFIER, shift_id UNIQUEIDENTIFIER);
            INSERT @r SELECT receipt_id, shift_id FROM gecko_revenue.cashier.receipt WHERE booking_id IN (SELECT id FROM @b);

            DELETE FROM gecko_revenue.outbox.message WHERE aggregate_id IN (SELECT id FROM @r) OR aggregate_id IN (SELECT id FROM @b)
                OR aggregate_id IN (SELECT charge_id FROM gecko_revenue.billing.charge WHERE booking_id IN (SELECT id FROM @b));
            DELETE FROM gecko_revenue.cashier.receipt_payment WHERE receipt_id IN (SELECT id FROM @r);
            DELETE FROM gecko_revenue.cashier.receipt_line    WHERE receipt_id IN (SELECT id FROM @r);
            DELETE FROM gecko_revenue.cashier.receipt         WHERE receipt_id IN (SELECT id FROM @r);
            DELETE FROM gecko_revenue.cashier.shift_count     WHERE shift_id IN (SELECT shift_id FROM @r) OR shift_id = @shift;
            DELETE FROM gecko_revenue.cashier.shift           WHERE shift_id IN (SELECT shift_id FROM @r) OR shift_id = @shift;
            DELETE FROM gecko_revenue.billing.charge          WHERE booking_id IN (SELECT id FROM @b);
            DELETE FROM gecko_revenue.billing.movement_pricing WHERE booking_id IN (SELECT id FROM @b);
            DELETE FROM gecko_revenue.billing.inbox           WHERE aggregate_id IN (SELECT id FROM @b)
                OR aggregate_id IN (SELECT gate_transaction_id FROM gate.gate_transaction WHERE booking_id IN (SELECT id FROM @b));
            DELETE FROM gecko_revenue.projection.container_stay WHERE in_booking_id IN (SELECT id FROM @b) OR out_booking_id IN (SELECT id FROM @b);
            DELETE FROM gecko_revenue.projection.booking_plan_container WHERE booking_id IN (SELECT id FROM @b);
            DELETE FROM gecko_revenue.projection.booking_plan WHERE booking_id IN (SELECT id FROM @b);
            """;
        command.Parameters.AddWithValue("@prefix", carrierRefPrefix);
        command.Parameters.AddWithValue("@shift", openShiftId is { } id ? id : DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The gate transaction that closed the box's latest stay in Revenue's projection, if one did.</summary>
    public static async Task<Guid?> StayClosedByAsync(string containerNo)
    {
        await using var connection = new SqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;
            SELECT TOP (1) out_gate_transaction_id FROM gecko_revenue.projection.container_stay
             WHERE container_no = @box AND status = 'CLOSED' ORDER BY out_at DESC;
            """;
        command.Parameters.AddWithValue("@box", containerNo);
        return await command.ExecuteScalarAsync() is Guid id ? id : null;
    }
}
