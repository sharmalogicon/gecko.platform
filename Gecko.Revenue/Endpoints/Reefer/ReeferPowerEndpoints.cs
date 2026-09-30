using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Reefer;

/// <param name="Outcome">PRICED | NO_SESSIONS | CHARGE_CODE_NOT_SET | RATE_NOT_SET | PRICED_ZERO.</param>
/// <param name="Amount">Null unless the tariff priced it — never a guessed figure.</param>
public sealed record ReeferPowerResponse(
    Guid ContainerVisitId, string ContainerNo, int BillableHours, int MinutesPlugged,
    string Outcome, string? ChargeCode, decimal? Amount, string? Currency, string Message);

/// <summary>
/// GET /api/revenue/reefer/power?containerVisitIds=… — what each visit's reefer
/// power costs AS AT NOW, for the TOS reefer page. Per started hour over the
/// visit's plug sessions (Revenue's copy of them), priced by the tenant's tariff;
/// when the charge code or the rate is not set, the outcome says so and the
/// amount is null.
///
/// The door is the TOS page's own permission (tos.reefer.view) or revenue's
/// tariff view; each visit is then shown only at a branch where the caller holds
/// one of them. A visit Revenue has not heard of — or another tenant's, which
/// RLS hides — is simply absent.
///
/// Indicative for a box still in the yard: priced with the box's open booking
/// (parties, order type, next OUT step) when Revenue has one, else on the
/// branch / public tariff. What is actually CHARGED is the cash window's quote.
/// </summary>
internal static class ReeferPowerEndpoints
{
    /// <summary>gecko_identity: the TOS reefer page's view permission. A string here — Revenue does not reference Gecko.Tos.</summary>
    public const string TosReeferView = "tos.reefer.view";

    public const int MaxVisits = 200;

    public static RouteGroupBuilder MapReeferPowerEndpoints(this RouteGroupBuilder revenue)
    {
        var reefer = revenue.MapGroup("/reefer").WithTags("Revenue — reefer power");

        reefer.MapGet("/power", PowerAsync)
            .RequireAuthorization(policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                {
                    var caller = new CallerPermissions(
                        context.User.FindAll(GeckoClaimTypes.Permission).Select(c => c.Value),
                        context.User.FindAll(GeckoClaimTypes.BranchPermission).Select(c => c.Value));
                    return caller.HasAnywhere(TosReeferView) || caller.HasAnywhere(RevenuePermissions.TariffView);
                }))
            .WithSummary("Reefer power per container visit as at now: started hours plugged in and what the tariff charges for them");

        return revenue;
    }

    private static async Task<Results<Ok<List<ReeferPowerResponse>>, ValidationProblem>> PowerAsync(
        string[]? containerVisitIds, RevenueDbContext db, ReeferPowerQuoter quoter, IMasterDataReferences master,
        ICallerPermissions permissions, TimeProvider clock, CancellationToken ct)
    {
        var tokens = (containerVisitIds ?? [])
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        if (tokens.Count == 0)
            return RevenueSupport.Invalid("containerVisitIds", "Name at least one container visit id (comma-separated or repeated).");
        var bad = tokens.Where(t => !Guid.TryParse(t, out _)).ToList();
        if (bad.Count > 0)
            return RevenueSupport.Invalid("containerVisitIds", $"Not a container visit id: {string.Join(", ", bad.Take(5))}.");
        var ids = tokens.Select(Guid.Parse).Distinct().ToList();
        if (ids.Count > MaxVisits)
            return RevenueSupport.Invalid("containerVisitIds", $"At most {MaxVisits} visits per call; {ids.Count} were named.");

        var sessions = (await db.ReeferSessions.AsNoTracking().Where(s => ids.Contains(s.ContainerVisitId)).ToListAsync(ct))
            .ToLookup(s => s.ContainerVisitId);
        var now = clock.GetUtcNow();
        var result = new List<ReeferPowerResponse>();

        foreach (var id in ids)
        {
            var visit = sessions[id].OrderByDescending(s => s.LastChangedAt).ToList();
            if (visit.Count == 0) continue;
            var latest = visit[0];
            if (!permissions.HasAt(TosReeferView, latest.BranchId) && !permissions.HasAt(RevenuePermissions.TariffView, latest.BranchId))
                continue;

            var context = await ContextAsync(db, master, latest, ct);
            var quote = await quoter.QuoteAsync(visit, context, now, ct);
            result.Add(new ReeferPowerResponse(id, latest.ContainerNo, quote.BillableHours, quote.MinutesPlugged,
                quote.Outcome, quote.ChargeCode, quote.Amount, quote.CurrencyCode, quote.Message));
        }

        return TypedResults.Ok(result);
    }

    /// <summary>The box's open booking and its next OUT step, as the window would price it; else no booking at all.</summary>
    private static async Task<ReeferPricingContext> ContextAsync(RevenueDbContext db, IMasterDataReferences master,
        ReeferSession session, CancellationToken ct)
    {
        var fallback = new ReeferPricingContext(session.BranchId, session.ContainerNo, session.EquipmentTypeCode);

        var candidate = await (
                from b in db.BookingPlanContainers.AsNoTracking()
                join p in db.BookingPlans.AsNoTracking() on b.BookingId equals p.BookingId
                where b.ContainerNo == session.ContainerNo && b.IsCurrent && b.EndReason == null
                      && p.Status == "OPEN" && p.BranchId == session.BranchId
                orderby p.UpdatedAt descending
                select new { Box = b, Plan = p })
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return fallback;

        var orderType = (await master.OrderTypePlansAsync([candidate.Plan.OrderTypeCode], ct)).GetValueOrDefault(candidate.Plan.OrderTypeCode);
        if (CashQuoter.NextStep(candidate.Box, orderType) is not (_, { Direction: "OUT" } rules)) return fallback;

        return new ReeferPricingContext(session.BranchId, session.ContainerNo, session.EquipmentTypeCode,
            candidate.Plan, candidate.Box, rules.MovementCode, rules.Direction, rules.FullEmpty);
    }
}
