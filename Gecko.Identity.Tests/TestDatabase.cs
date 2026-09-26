using Gecko.Data;
using Gecko.Identity.Infrastructure.Persistence;
using Gecko.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Tests;

/// <summary>
/// Builds DbContexts against the LOCAL dev gecko_identity with the real SQL logins,
/// so the tests exercise the actual RLS policy and DENYs — a mock cannot.
///
/// Credentials are the dev ones from 09_security_hardening.sql. Override with
/// GECKO_IDENTITY_APP / GECKO_IDENTITY_SYSTEM environment variables elsewhere.
/// </summary>
internal static class TestDatabase
{
    private const string Server = @"DESKTOP-6AQI384\APPIFY";

    public static readonly string AppConnection =
        Environment.GetEnvironmentVariable("GECKO_IDENTITY_APP")
        ?? $"Server={Server};Database=gecko_identity;User Id=gecko_app;Password=GeckoApp#Dev2026!;TrustServerCertificate=true";

    public static readonly string SystemConnection =
        Environment.GetEnvironmentVariable("GECKO_IDENTITY_SYSTEM")
        ?? $"Server={Server};Database=gecko_identity;User Id=gecko_system;Password=GeckoSys#Dev2026!;TrustServerCertificate=true";

    // Fixture tenants (dev_01_sample_tenants.sql).
    public static readonly Guid Sct = Guid.Parse("C458E785-33A5-F111-9B0D-00919E4766D5");
    public static readonly Guid Sss = Guid.Parse("7259E785-33A5-F111-9B0D-00919E4766D5");

    public static IdentityDbContext ForTenant(Guid? tenantId, string? connection = null)
    {
        var caller = new FixedTenantContext(tenantId, UserId: null);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlServer(connection ?? AppConnection)
            .AddInterceptors(new TenantSessionInterceptor(caller), new AuditStampInterceptor(caller, TimeProvider.System))
            .Options;
        return new IdentityDbContext(options);
    }

    public static IdentitySystemDbContext ForSystem(string? connection = null)
    {
        var options = new DbContextOptionsBuilder<IdentitySystemDbContext>()
            .UseSqlServer(connection ?? SystemConnection)
            .AddInterceptors(new SystemSessionInterceptor(), new AuditStampInterceptor(new FixedTenantContext(null, null), TimeProvider.System))
            .Options;
        return new IdentitySystemDbContext(options);
    }

    internal sealed record FixedTenantContext(Guid? TenantId, Guid? UserId) : ITenantContext;
}
