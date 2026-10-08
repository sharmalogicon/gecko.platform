using System.Text.Json;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Application;

/// <summary>
/// <c>BookingChanged</c> — the whole booking as it now stands, queued in the same
/// transaction as the change (11_outbox). Revenue keeps its own copy from these
/// (PLAN_BILLING §4.2 step 0) so the cashier window can quote a box before the
/// truck arrives, without Revenue ever reading gecko_tos (ADR-007).
///
/// One message type carrying the full state rather than one per edit: a consumer
/// that missed or reordered an earlier message still ends up right by keeping the
/// highest message id it has seen.
/// </summary>
internal static class BookingEvents
{
    public const string Changed = "BookingChanged";

    public static async Task QueueChangedAsync(TosDbContext db, Guid bookingId, string reason, DateTimeOffset at, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == bookingId, ct);

        var requirements = await db.EquipmentRequirements.AsNoTracking()
            .Where(r => r.BookingId == bookingId && r.DeletedAt == null)
            .OrderBy(r => r.LineNo)
            .Select(r => new
            {
                equipmentRequirementId = r.EquipmentRequirementId,
                lineNo = r.LineNo,
                equipmentTypeCode = r.EquipmentTypeCode,
                qty = r.Qty,
                isDangerousGoods = (r.UnNumber != null && r.UnNumber != "") || (r.ImdgClass != null && r.ImdgClass != ""),
                isReefer = r.ReeferSetTempC != null,
                declaredGrossWeightKg = r.DeclaredGrossWeightKg,
            })
            .ToListAsync(ct);

        var containers = await db.BookingContainers.AsNoTracking()
            .Where(x => x.BookingId == bookingId && x.DeletedAt == null)
            .OrderBy(x => x.AssignedAt)
            .Select(x => new
            {
                bookingContainerId = x.BookingContainerId,
                equipmentRequirementId = x.EquipmentRequirementId,
                containerNo = x.ContainerNo,
                endReason = x.EndReason,
                steps = db.MovementPlans
                    .Where(p => p.BookingContainerId == x.BookingContainerId && p.DeletedAt == null)
                    .OrderBy(p => p.SequenceNo)
                    .Select(p => new { sequenceNo = p.SequenceNo, movementCode = p.MovementCode, status = p.Status })
                    .ToList(),
            })
            .ToListAsync(ct);

        var payload = JsonSerializer.Serialize(new
        {
            bookingId = booking.BookingId,
            branchId = booking.BranchId,
            orderNo = booking.OrderNo,
            reason,
            changedAt = at,
            status = booking.Status,
            orderTypeCode = booking.OrderTypeCode,
            bookingTypeCode = booking.BookingTypeCode,
            directionCode = booking.DirectionCode,
            lineCode = booking.LinePartyCode,
            agentPartyCode = booking.AgentPartyCode,
            customerCode = booking.CustomerPartyCode,
            forwarderPartyCode = booking.ForwarderPartyCode,
            haulierPartyCode = booking.HaulierPartyCode,
            cargoClassCode = booking.CargoClassCode,
            cargoCategoryCode = booking.CargoCategoryCode,
            validFrom = booking.ValidFrom,
            validTo = booking.ValidTo,
            requirements,
            containers,
        });

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO outbox.message (tenant_id, aggregate_type, aggregate_id, message_type, payload_json) " +
            "VALUES ({0}, 'BOOKING', {1}, {2}, {3})",
            [booking.TenantId, booking.BookingId, Changed, payload],
            ct);
    }

    public const string Transferred = "ContainersTransferred";

    /// <summary>
    /// <c>ContainersTransferred</c>: these boxes now belong to <paramref name="target"/>. Revenue moves
    /// their statement lines (Vector moved BookingStatement and InvoiceDetail with the box). Queued
    /// before the two bookings' BookingChanged, so the source's re-quote does not cancel them.
    /// </summary>
    public static async Task QueueTransferredAsync(TosDbContext db, Booking source, Booking target, IEnumerable<Guid> boxes, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sourceBookingId = source.BookingId,
            targetBookingId = target.BookingId,
            targetOrderNo = target.OrderNo,
            bookingContainerIds = boxes.ToList(),
        });

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO outbox.message (tenant_id, aggregate_type, aggregate_id, message_type, payload_json) " +
            "VALUES ({0}, 'BOOKING', {1}, {2}, {3})",
            [source.TenantId, source.BookingId, Transferred, payload],
            ct);
    }

    public const string Cloned = "BookingCloned";

    /// <summary>
    /// <c>BookingCloned</c> (owner 2026-10-07): the new booking, its source, and which source box
    /// each new place copies. Revenue copies the source's manual lines and price corrections.
    /// Queued after the clone's BookingChanged, in the same transaction.
    /// </summary>
    public static async Task QueueClonedAsync(TosDbContext db, Booking booking, Guid sourceBookingId,
        IEnumerable<(Guid Box, Guid? Source)> boxes, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            bookingId = booking.BookingId,
            sourceBookingId,
            containers = boxes.Where(b => b.Source is not null)
                .Select(b => new { bookingContainerId = b.Box, sourceBookingContainerId = b.Source!.Value }).ToList(),
        });

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO outbox.message (tenant_id, aggregate_type, aggregate_id, message_type, payload_json) " +
            "VALUES ({0}, 'BOOKING', {1}, {2}, {3})",
            [booking.TenantId, booking.BookingId, Cloned, payload],
            ct);
    }
}
