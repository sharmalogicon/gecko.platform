using Gecko.Data;
using Gecko.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Endpoints.Admin;

public sealed record EntitlementResponse(
    Guid EntitlementId, string ModuleCode, Guid? BranchId, string? BranchCode, Guid PlanId, string PlanCode, string PlanName,
    string Status, string BillingCycle, decimal MonthlyPrice, string Currency, bool IsPriceOverridden,
    DateTimeOffset? TrialEndsAt, DateTimeOffset? CurrentPeriodStart, DateTimeOffset? CurrentPeriodEnd, bool CancelAtPeriodEnd);

public sealed record PlanLimitResponse(string MetricCode, string DisplayName, long? LimitValue, string Period);

public sealed record PlanResponse(
    Guid PlanId, string ModuleCode, string PlanCode, string DisplayName, string? Description, int TierLevel,
    decimal PriceMonthly, decimal PriceYearly, string Currency, IReadOnlyList<PlanLimitResponse> Limits);

public sealed record UsageResponse(Guid EntitlementId, string ModuleCode, string MetricCode, DateOnly PeriodStart, long UsedValue, long? LimitSnapshot, DateTimeOffset LastUpdatedAt);

public sealed record InvoiceSummary(Guid InvoiceId, string InvoiceNumber, DateOnly PeriodStart, DateOnly PeriodEnd, decimal TotalAmount, string Currency, string Status, DateTimeOffset? IssuedAt, DateTimeOffset? DueAt, DateTimeOffset? PaidAt);

public sealed record InvoiceLineResponse(Guid InvoiceLineId, string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal, Guid? EntitlementId);

public sealed record InvoiceResponse(
    Guid InvoiceId, string InvoiceNumber, DateOnly PeriodStart, DateOnly PeriodEnd, decimal Subtotal, decimal TaxAmount, decimal TotalAmount,
    string Currency, string Status, DateTimeOffset? IssuedAt, DateTimeOffset? DueAt, DateTimeOffset? PaidAt, string? PaymentReference,
    IReadOnlyList<InvoiceLineResponse> Lines);

/// <summary>
/// What GECKO bills the TENANT (the SaaS subscription) — read-only for tenants.
/// Not to be confused with the depot billing the shipping line, which is the
/// Revenue context (ADR-007 naming collision).
/// </summary>
internal static class SubscriptionEndpoints
{
    private const decimal MonthsPerYear = 12m;

    public static RouteGroupBuilder MapSubscriptionEndpoints(this RouteGroupBuilder api)
    {
        var subscription = api.MapGroup("/subscription").WithTags("Subscription (read-only)").RequirePermission(Permissions.BillingView);

        subscription.MapGet("/entitlements", EntitlementsAsync).WithSummary("Modules the tenant subscribes to, per branch");
        subscription.MapGet("/plans", PlansAsync).WithSummary("Public plans plus the plans the tenant is on");
        subscription.MapGet("/usage", UsageAsync).WithSummary("Metered usage counters");
        subscription.MapGet("/invoices", InvoicesAsync).WithSummary("Invoices from GECKO to the tenant");
        subscription.MapGet("/invoices/{invoiceId:guid}", InvoiceAsync).WithSummary("One invoice with its lines");

        return api;
    }

    private static async Task<Ok<List<EntitlementResponse>>> EntitlementsAsync(IdentityDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await (
            from e in db.Entitlements.AsNoTracking()
            join p in db.Plans on e.PlanId equals p.PlanId
            orderby e.ModuleCode
            select new EntitlementResponse(
                e.EntitlementId, e.ModuleCode, e.BranchId,
                db.Branches.Where(b => b.BranchId == e.BranchId).Select(b => b.BranchCode).FirstOrDefault(),
                p.PlanId, p.PlanCode, p.DisplayName, e.Status, e.BillingCycle,
                e.PriceOverride ?? (e.BillingCycle == "YEARLY" ? p.PriceYearly / MonthsPerYear : p.PriceMonthly),
                e.CurrencyOverride ?? p.Currency,
                e.PriceOverride != null,
                e.TrialEndsAt, e.CurrentPeriodStart, e.CurrentPeriodEnd, e.CancelAtPeriodEnd))
            .ToListAsync(ct));

    /// <summary>
    /// subscription.plan has no tenant_id, so RLS does not apply. Non-public plans carry
    /// placeholder prices ("CONFIRM BEFORE QUOTING", 13_pricing_thb.sql) and must not leak
    /// to a tenant unless that tenant is actually on the plan.
    /// </summary>
    private static async Task<Ok<List<PlanResponse>>> PlansAsync(IdentityDbContext db, CancellationToken ct)
    {
        var subscribedPlanIds = db.Entitlements.Select(e => e.PlanId);

        return TypedResults.Ok(await db.Plans.AsNoTracking()
            .Where(p => p.IsActive && (p.IsPublic || subscribedPlanIds.Contains(p.PlanId)))
            .OrderBy(p => p.ModuleCode).ThenBy(p => p.TierLevel)
            .Select(p => new PlanResponse(
                p.PlanId, p.ModuleCode, p.PlanCode, p.DisplayName, p.Description, p.TierLevel, p.PriceMonthly, p.PriceYearly, p.Currency,
                db.PlanLimits.Where(l => l.PlanId == p.PlanId).OrderBy(l => l.MetricCode)
                    .Select(l => new PlanLimitResponse(l.MetricCode, l.DisplayName, l.LimitValue, l.Period)).ToList()))
            .ToListAsync(ct));
    }

    private static async Task<Ok<List<UsageResponse>>> UsageAsync(IdentityDbContext db, CancellationToken ct, DateOnly? from = null, DateOnly? to = null)
    {
        var counters = db.UsageCounters.AsNoTracking();
        if (from is not null) counters = counters.Where(u => u.PeriodStart >= from);
        if (to is not null) counters = counters.Where(u => u.PeriodStart <= to);

        return TypedResults.Ok(await (
            from u in counters
            join e in db.Entitlements on u.EntitlementId equals e.EntitlementId
            orderby u.PeriodStart descending, e.ModuleCode, u.MetricCode
            select new UsageResponse(u.EntitlementId, e.ModuleCode, u.MetricCode, u.PeriodStart, u.UsedValue, u.LimitSnapshot, u.LastUpdatedAt))
            .ToListAsync(ct));
    }

    private static async Task<Ok<PagedResult<InvoiceSummary>>> InvoicesAsync(
        [AsParameters] ListQuery query, IdentityDbContext db, CancellationToken ct, string? status = null)
    {
        var invoices = db.Invoices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) invoices = invoices.Where(i => i.Status == status.ToUpperInvariant());

        return TypedResults.Ok(await invoices
            .OrderByDescending(i => i.PeriodStart)
            .Select(i => new InvoiceSummary(i.InvoiceId, i.InvoiceNumber, i.PeriodStart, i.PeriodEnd, i.TotalAmount, i.Currency, i.Status, i.IssuedAt, i.DueAt, i.PaidAt))
            .ToPagedAsync(query.Page, query.PageSize, ct));
    }

    private static async Task<Results<Ok<InvoiceResponse>, NotFound>> InvoiceAsync(Guid invoiceId, IdentityDbContext db, CancellationToken ct) =>
        await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceId == invoiceId)
            .Select(i => new InvoiceResponse(
                i.InvoiceId, i.InvoiceNumber, i.PeriodStart, i.PeriodEnd, i.Subtotal, i.TaxAmount, i.TotalAmount, i.Currency, i.Status,
                i.IssuedAt, i.DueAt, i.PaidAt, i.PaymentReference,
                db.InvoiceLines.Where(l => l.InvoiceId == i.InvoiceId).OrderBy(l => l.SortOrder)
                    .Select(l => new InvoiceLineResponse(l.InvoiceLineId, l.Description, l.Quantity, l.UnitPrice, l.LineTotal, l.EntitlementId)).ToList()))
            .SingleOrDefaultAsync(ct) is { } invoice
            ? TypedResults.Ok(invoice)
            : TypedResults.NotFound();
}
