using Gecko.Data;
using Gecko.Data.Documents;
using Gecko.Tos.Application;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Dashboard;
using Gecko.Tos.Endpoints.Gate;
using Gecko.Tos.Endpoints.Holds;
using Gecko.Tos.Endpoints.Vessels;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gecko.Tos;

/// <summary>
/// The TOS context (ADR-007): what happens to a box in this depot — the vessel
/// it is going to, the booking it is on, where it is, what stops it moving.
/// Phase 3 batch A = vessel calls, batch B = bookings, batch C = holds and
/// cut-off exceptions. The gate is Phase 5.
///
/// ONE connection (gecko_app on gecko_tos), like Revenue: every request is
/// RLS-scoped. Master-data codes are checked through <c>IMasterDataReferences</c>
/// at admin time only — the gate barrier must never call it (coupon pattern).
/// </summary>
public static class TosModule
{
    /// <summary>Login gecko_app on gecko_tos: RLS-scoped, cannot DELETE, cannot write config.* or lookup.*.</summary>
    public const string AppConnectionName = "TosApp";

    public const string RoutePrefix = "/api/tos";

    public static IServiceCollection AddTosModule(this IServiceCollection services, IConfiguration configuration)
    {
        var appConnection = configuration.GetConnectionString(AppConnectionName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"ConnectionStrings:{AppConnectionName} is not set. Set it with: dotnet user-secrets set \"ConnectionStrings:{AppConnectionName}\" \"...\" --project Gecko.Api");

        services.AddGeckoTenancy();

        services.AddDbContext<TosDbContext>((sp, options) => options
            .UseSqlServer(appConnection)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>(), sp.GetRequiredService<AuditStampInterceptor>()));

        services.AddScoped<BranchClock>();
        services.AddScoped<BarrierReader>();
        services.AddScoped<EirDocument>();
        services.AddGeckoFileStore(configuration);

        // How Revenue's cash window reaches the barrier: GateCouponIssued, off
        // Revenue's outbox, written here as gate.gate_authorization (ADR-007).
        services.AddScoped<IOutboxHandler, CouponHandler>();

        return services;
    }

    public static IEndpointRouteBuilder MapTosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup(RoutePrefix)
            .MapVesselCallEndpoints()
            .MapBookingEndpoints()
            .MapHoldEndpoints()
            .MapGateEndpoints()
            .MapSurveyEndpoints()
            .MapAttachmentEndpoints()
            .MapDashboardEndpoints();

        return endpoints;
    }
}
