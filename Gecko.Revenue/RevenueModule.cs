using Gecko.Data;
using Gecko.Revenue.Application;
using Gecko.Revenue.Endpoints.Charges;
using Gecko.Revenue.Endpoints.Reports;
using Gecko.Revenue.Endpoints.Imports;
using Gecko.Revenue.Endpoints.Reefer;
using Gecko.Revenue.Endpoints.Tariffs;
using Gecko.Revenue.Endpoints.Window;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gecko.Revenue;

/// <summary>
/// The Revenue context (ADR-007): what the depot charges its customers.
/// Phase 2 = tariffs. Accrual, coupons, cashiering and invoices arrive in Phase 6.
///
/// NOT subscription billing — that is Identity, and money flows the other way.
///
/// ONE connection (gecko_app on gecko_revenue), like MasterData: nothing here
/// runs before authentication, so every request is RLS-scoped.
///
/// Master-data codes are checked through <c>IMasterDataReferences</c>
/// (Gecko.MasterData.Contracts) — never by reaching into gecko_master.
/// </summary>
public static class RevenueModule
{
    /// <summary>Login gecko_app on gecko_revenue: RLS-scoped, cannot DELETE, cannot write lookup.*.</summary>
    public const string AppConnectionName = "RevenueApp";

    public const string RoutePrefix = "/api/revenue";

    public static IServiceCollection AddRevenueModule(this IServiceCollection services, IConfiguration configuration)
    {
        var appConnection = configuration.GetConnectionString(AppConnectionName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"ConnectionStrings:{AppConnectionName} is not set. Set it with: dotnet user-secrets set \"ConnectionStrings:{AppConnectionName}\" \"...\" --project Gecko.Api");

        services.AddGeckoTenancy();

        services.AddDbContext<RevenueDbContext>((sp, options) => options
            .UseSqlServer(appConnection)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>(), sp.GetRequiredService<AuditStampInterceptor>()));

        services.AddScoped<BranchCalendar>();
        services.AddScoped<RateSetValidator>();
        services.AddScoped<ScheduleApprovalGuard>();
        services.AddScoped<Contracts.ITariffPricing, TariffPricer>();
        services.AddScoped<Contracts.ITruckCashier, Endpoints.Window.TruckCashier>();

        // Phase 6, clock 1 — the cash window (PLAN_BILLING §4.2).
        services.AddScoped<ReeferPowerQuoter>();
        services.AddScoped<CashQuoter>();
        services.AddScoped<WindowService>();
        services.AddScoped<ReceiptDocument>();
        services.AddScoped<AutomaticCoupons>();

        // What Revenue hears from TOS, off gecko_tos's outbox (ADR-007: it never reads gecko_tos).
        services.AddScoped<IOutboxHandler, BookingChangedHandler>();
        services.AddScoped<IOutboxHandler, GateEventHandler>();
        services.AddScoped<IOutboxHandler, ReeferSessionHandler>();

        return services;
    }

    public static IEndpointRouteBuilder MapRevenueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup(RoutePrefix)
            .MapScheduleEndpoints()
            .MapRateEndpoints()
            .MapPricingEndpoints()
            .MapLookupEndpoints()
            .MapImportEndpoints()
            .MapWindowEndpoints()
            .MapReeferPowerEndpoints()
            .MapChargeEndpoints()
            .MapUnbilledOrderEndpoints()
            .MapReceiptReportEndpoints();

        return endpoints;
    }
}
