using Gecko.Data;
using Gecko.MasterData.Application;
using Gecko.MasterData.Endpoints.Commercial;
using Gecko.MasterData.Endpoints.Config;
using Gecko.MasterData.Endpoints.Equipment;
using Gecko.MasterData.Endpoints.Logistics;
using Gecko.MasterData.Endpoints.Org;
using Gecko.MasterData.Endpoints.Parties;
using Gecko.MasterData.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gecko.MasterData;

/// <summary>
/// The module's only entry point from the host. Gecko.Api calls Add + Map and
/// knows nothing else about what is inside.
///
/// ONE connection, unlike Identity. Identity needs a second gecko_system login
/// because login has to read a password hash before a tenant is known. Nothing
/// in master data runs before authentication, so every request here is
/// RLS-scoped through gecko_app. The system login stays for the outbox consumer
/// that will write org.branch_profile from Identity's BranchCreated event —
/// which does not exist yet and gets its own connection when it does.
/// </summary>
public static class MasterDataModule
{
    /// <summary>Login gecko_app on gecko_master: RLS-scoped, cannot DELETE, cannot write lookup.*.</summary>
    public const string AppConnectionName = "MasterDataApp";

    /// <summary>
    /// All master-data routes sit under this prefix. Identity already owns
    /// /api/branches and /api/lookups; two modules competing for the same path
    /// is a collision waiting for the third module, so each module gets its own.
    /// </summary>
    public const string RoutePrefix = "/api/master";

    public static IServiceCollection AddMasterDataModule(this IServiceCollection services, IConfiguration configuration)
    {
        var appConnection = RequireConnectionString(configuration, AppConnectionName);

        services.AddGeckoTenancy();

        services.AddDbContext<MasterDataDbContext>((sp, options) => options
            .UseSqlServer(appConnection)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>(), sp.GetRequiredService<AuditStampInterceptor>()));

        services.AddScoped<TenantSettingReader>();
        services.AddScoped<Contracts.IMasterDataReferences, MasterDataReferences>();

        return services;
    }

    public static IEndpointRouteBuilder MapMasterDataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup(RoutePrefix)
            .MapEquipmentTypeEndpoints()
            .MapEquipmentVocabulary()
            .MapIsoCodeEndpoints()
            .MapContainerEndpoints()
            .MapEquipmentCodeEndpoints()
            .MapCommercialEndpoints()
            .MapCommercialVocabulary()
            .MapOrderTypeEndpoints()
            .MapConfigEndpoints()
            .MapPartyEndpoints()
            .MapContactEndpoints()
            .MapShippingLineEndpoints()
            .MapOrgEndpoints()
            .MapPortEndpoints()
            .MapVesselEndpoints()
            .MapCommodityEndpoints()
            .MapLocationEndpoints()
            .MapSealRangeEndpoints()
            .MapCalendarEndpoints()
            .MapSurveyCodeEndpoints();

        return endpoints;
    }

    private static string RequireConnectionString(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"ConnectionStrings:{name} is not set. Set it with: dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\" --project Gecko.Api");
}
