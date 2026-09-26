using Gecko.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Gecko.Identity.Infrastructure.Persistence;

/// <summary>
/// A tenant-scoped IdentityDbContext for a tenant that is known in code but not
/// yet in a token — i.e. login, the moment after the password verifies.
/// Rule 5 of 06_rls_security.sql: once the password verifies, stop using system
/// context and read everything else through RLS like any other request.
/// </summary>
public sealed class TenantDbContextFactory(IConfiguration configuration, TimeProvider clock)
{
    public IdentityDbContext Create(Guid tenantId, Guid? userId = null)
    {
        var caller = new ExplicitTenantContext(tenantId, userId);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlServer(configuration.GetConnectionString(IdentityModule.AppConnectionName))
            .AddInterceptors(new TenantSessionInterceptor(caller), new AuditStampInterceptor(caller, clock))
            .Options;

        return new IdentityDbContext(options);
    }
}
