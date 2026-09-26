using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gecko.Data;

/// <summary>A tenant context set explicitly — for code that runs before or outside a request's token (login, jobs, tests).</summary>
public sealed record ExplicitTenantContext(Guid? TenantId, Guid? UserId) : ITenantContext;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the request tenant context and the interceptors. Every module
    /// calls this; TryAdd makes the repeats harmless.
    /// </summary>
    public static IServiceCollection AddGeckoTenancy(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<OutboxTenantScope>();
        services.TryAddScoped<ITenantContext, ClaimsTenantContext>();
        services.TryAddScoped<ICallerPermissions, ClaimsCallerPermissions>();
        services.TryAddScoped<TenantSessionInterceptor>();
        services.TryAddScoped<AuditStampInterceptor>();
        services.TryAddSingleton<SystemSessionInterceptor>();
        return services;
    }

    /// <summary>
    /// Endpoint requires a tenant-wide permission from the access token's <c>prm</c> claim.
    /// Permissions, not role names: a tenant can rename or clone a role without breaking authorization.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(GeckoClaimTypes.Permission, permission));

    /// <summary>
    /// Endpoint acts on rows that belong to a BRANCH, so a branch-scoped grant
    /// (<c>bpm</c>) opens the door as well as a tenant-wide one — gate clerks and
    /// depot supervisors work at one depot (gecko_tos PLAN Q11).
    ///
    /// This is only the door. The handler must still scope what it reads and
    /// writes with <see cref="ICallerPermissions.HasAt"/> /
    /// <see cref="ICallerPermissions.BranchesFor"/>; passing this policy means
    /// "somewhere", never "here". Tenant-wide endpoints keep
    /// <see cref="RequirePermission"/>, so a branch grant of, say,
    /// <c>admin.user.manage</c> still cannot act on the whole tenant.
    /// </summary>
    public static TBuilder RequireBranchPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                new CallerPermissions(
                    context.User.FindAll(GeckoClaimTypes.Permission).Select(c => c.Value),
                    context.User.FindAll(GeckoClaimTypes.BranchPermission).Select(c => c.Value))
                .HasAnywhere(permission)));
}
