using System.Data;
using System.Text.Json;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary>
/// Revenue's own document numbers (PLAN_BILLING §4.7): RCT-KTC-2609-00001.
/// gecko_app cannot touch config.*; a counter only moves through
/// config.usp_next_number, INSIDE the caller's transaction — every series here is
/// gap-free, and the procedure refuses to number one outside a transaction.
/// </summary>
internal static class RevenueNumberSeries
{
    public const string Receipt = "RECEIPT";
    public const string Invoice = "INVOICE";
    public const string CreditNote = "CREDIT_NOTE";

    /// <param name="localNow">Branch-local "now": the number's YYMM, and its counter, is the depot's month.</param>
    public static async Task<string> NextAsync(RevenueDbContext db, string seriesKey, Guid branchId, string branchCode,
        DateTimeOffset localNow, CancellationToken ct)
    {
        var number = new SqlParameter("@number", SqlDbType.NVarChar, 60) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "EXEC config.usp_next_number @series_key = @key, @branch_id = @branch, @branch_code = @code, @at = @at, @number = @number OUTPUT",
            [
                new SqlParameter("@key", SqlDbType.VarChar, 30) { Value = seriesKey },
                new SqlParameter("@branch", SqlDbType.UniqueIdentifier) { Value = branchId },
                new SqlParameter("@code", SqlDbType.VarChar, 30) { Value = branchCode },
                new SqlParameter("@at", SqlDbType.DateTimeOffset) { Value = localNow },
                number,
            ],
            ct);
        return (string)number.Value;
    }
}

/// <summary>
/// What Revenue tells the rest of the platform, written into gecko_revenue's
/// outbox (16_outbox) in the SAME transaction as the change: a receipt that rolls
/// back never releases a box. gecko_app may INSERT here and may not read.
/// </summary>
internal static class RevenueOutbox
{
    public const string CouponIssued = "GateCouponIssued";
    public const string CouponRevoked = "GateCouponRevoked";

    public static Task EnqueueAsync(RevenueDbContext db, Guid tenantId, string aggregateType, Guid? aggregateId,
        string messageType, object payload, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "INSERT INTO outbox.message (tenant_id, aggregate_type, aggregate_id, message_type, payload_json) VALUES ({0}, {1}, {2}, {3}, {4})",
            [tenantId, aggregateType, (object?)aggregateId ?? DBNull.Value, messageType, JsonSerializer.Serialize(payload)],
            ct);
}

/// <summary>
/// The coupon as TOS reads it (Gecko.Tos CouponHandler.CouponIssued). CouponId is
/// both the row id and the idempotency key there.
/// </summary>
internal sealed record CouponIssuedPayload(
    Guid CouponId, Guid BranchId, Guid BookingId, string? ContainerNo, string MovementCode,
    string CouponRef, string Channel, decimal? Amount, string? CurrencyCode,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, Guid? IssuedBy);
