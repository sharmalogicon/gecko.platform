using Gecko.Revenue.Domain;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary>
/// The rules a tariff must pass to be submitted and approved that no CHECK or
/// unique index can express — PLAN.md "Carried into 2.6", items 3 and 4.
/// Each method returns the reasons it refuses; empty means go.
/// </summary>
internal sealed class ScheduleApprovalGuard(RevenueDbContext db)
{
    /// <summary>Before a version may be sent for approval.</summary>
    public async Task<IReadOnlyList<string>> SubmitProblemsAsync(Schedule schedule, CancellationToken ct)
    {
        var problems = new List<string>();

        var rateCount = await db.TosRates.CountAsync(r => r.ScheduleId == schedule.ScheduleId, ct);
        if (rateCount == 0)
            problems.Add("A tariff with no rates prices nothing. Add rates before submitting.");

        var defects = await db.VwRateTierDefects.AsNoTracking()
            .Where(d => d.ScheduleId == schedule.ScheduleId)
            .Select(d => d.ChargeCode + ": " + d.Defect)
            .ToListAsync(ct);
        if (defects.Count > 0)
            problems.Add("Tier problems: " + string.Join("; ", defects.Distinct()) + ".");

        // PLAN.md Q4: the PUBLIC schedule is the source of free storage days.
        // Without a rule the resolver would charge from day one.
        if (schedule is { ScheduleType: ScheduleTypes.Public, ModuleCode: "TOS" }
            && !await db.FreeTimeRules.AnyAsync(f => f.ScheduleId == schedule.ScheduleId && f.FreeTimeKind == FreeTimeKinds.Storage, ct))
            problems.Add("A public TOS tariff must state its free storage days (a STORAGE free-time rule, even if it is 0).");

        // A new version cannot start before the version it replaces — that would
        // silently erase the old one from the day it began.
        if (schedule.SupersedesScheduleId is { } previousId)
        {
            var previousFrom = await db.Schedules.AsNoTracking()
                .Where(s => s.ScheduleId == previousId)
                .Select(s => (DateOnly?)s.EffectiveFrom)
                .SingleOrDefaultAsync(ct);
            if (previousFrom is { } from && schedule.EffectiveFrom <= from)
                problems.Add($"Version {schedule.VersionNo} must start after version {schedule.VersionNo - 1} (which starts {from:yyyy-MM-dd}).");
        }

        return problems;
    }

    /// <summary>
    /// Two APPROVED agreements with the SAME scope in force on the same day give
    /// the resolver two candidates of equal rank and no rule to choose — refuse
    /// the second one. Versions of one agreement are not a clash: a later version
    /// supersedes an earlier one by design.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApproveProblemsAsync(Schedule schedule, CancellationToken ct)
    {
        var until = schedule.EffectiveTo ?? DateOnly.MaxValue;

        var clashes = await db.VwScheduleEffectives.AsNoTracking()
            .Where(e => e.LineageId != schedule.LineageId
                && e.IsFullySuperseded == false
                && e.ModuleCode == schedule.ModuleCode
                && e.ScheduleType == schedule.ScheduleType
                && e.BranchId == schedule.BranchId
                && e.AgentPartyId == schedule.AgentPartyId
                && e.ForwarderPartyId == schedule.ForwarderPartyId
                && e.CustomerPartyId == schedule.CustomerPartyId
                && e.BookingRef == schedule.BookingRef
                && e.EffectiveFrom <= until
                && (e.EffectiveUntil == null || e.EffectiveUntil >= schedule.EffectiveFrom))
            .Select(e => new { e.ScheduleNo, e.VersionNo, e.EffectiveFrom, e.EffectiveUntil })
            .ToListAsync(ct);

        return clashes
            .Select(c => $"{c.ScheduleNo} v{c.VersionNo} already covers the same customers from {c.EffectiveFrom:yyyy-MM-dd}"
                         + (c.EffectiveUntil is { } u ? $" to {u:yyyy-MM-dd}" : " with no end date")
                         + ". End it with a new version, or change this tariff's dates.")
            .ToList();
    }
}
