using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Tests;

/// <summary>
/// Builds a MasterDataDbContext against the LOCAL dev gecko_master with the real
/// gecko_app login, so these tests exercise the actual RLS policy and DENYs — a
/// mock cannot. Credentials are the dev ones from 01_security_foundation.sql.
/// </summary>
internal static class TestDatabase
{
    private const string Server = @"DESKTOP-6AQI384\APPIFY";

    public static readonly string AppConnection =
        Environment.GetEnvironmentVariable("GECKO_MASTER_APP")
        ?? $"Server={Server};Database=gecko_master;User Id=gecko_app;Password=GeckoApp#Dev2026!;TrustServerCertificate=true";

    // Owner-level, for ApiRowLeakGuard.PurgeSoftDeletedAsync only: gecko_app may not hard-delete.
    public static readonly string AdminConnection =
        Environment.GetEnvironmentVariable("GECKO_MASTER_ADMIN")
        ?? $"Server={Server};Database=gecko_master;Integrated Security=true;TrustServerCertificate=true";

    // Fixture tenants — the same GUIDs gecko_identity provisioned.
    public static readonly Guid Sct = Guid.Parse("C458E785-33A5-F111-9B0D-00919E4766D5");
    // SIAM-COMMERCIAL is the "other tenant" in these tests. KORAKIT is a real client now, loaded
    // from Vector by database/_migration/other, so no test may depend on its rows.
    public static readonly Guid SiamCommercial = Guid.Parse("205AE785-33A5-F111-9B0D-00919E4766D5");

    public static MasterDataDbContext ForTenant(Guid? tenantId)
    {
        var caller = new FixedTenantContext(tenantId, UserId: null);
        var options = new DbContextOptionsBuilder<MasterDataDbContext>()
            .UseSqlServer(AppConnection)
            .AddInterceptors(new TenantSessionInterceptor(caller), new AuditStampInterceptor(caller, TimeProvider.System))
            .Options;
        return new MasterDataDbContext(options);
    }

    internal sealed record FixedTenantContext(Guid? TenantId, Guid? UserId) : ITenantContext;
}

/// <summary>
/// The guarantees gecko_master rests on, checked through EF rather than sqlcmd —
/// because the API reaches the database this way, and an interceptor that is
/// registered but not wired would pass _tools/verify_isolation.sh and still leak.
/// </summary>
public sealed class TenantIsolationTests
{
    [Fact]
    public async Task A_context_with_no_tenant_refuses_to_open_a_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDatabase.ForTenant(null);

        // Not "returns nothing" — throws. RLS is fail-closed, so an unscoped query
        // would quietly return an empty list, and empty-list bugs ship.
        await Assert.ThrowsAsync<MissingTenantContextException>(
            () => db.EquipmentTypes.AnyAsync(ct));
    }

    [Fact]
    public async Task A_tenant_sees_only_its_own_equipment_types()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sct = TestDatabase.ForTenant(TestDatabase.Sct);
        await using var other = TestDatabase.ForTenant(TestDatabase.SiamCommercial);

        var sctCodes = await sct.EquipmentTypes.Select(t => t.TypeCode).ToListAsync(ct);
        var otherCodes = await other.EquipmentTypes.Select(t => t.TypeCode).ToListAsync(ct);

        Assert.Contains("20GP", sctCodes);
        Assert.DoesNotContain("20DV", sctCodes);
        Assert.Contains("20DV", otherCodes);
        Assert.DoesNotContain("20GP", otherCodes);
    }

    /// <summary>
    /// The mapping table is where a broken RLS policy would show up first: both
    /// tenants map 22G1, so an unscoped read returns two rows and SingleAsync throws.
    /// </summary>
    [Fact]
    public async Task The_same_iso_code_maps_to_exactly_one_local_type_within_a_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sct = TestDatabase.ForTenant(TestDatabase.Sct);

        var mapping = await sct.EquipmentTypeIsoCodes
            .Where(m => m.IsoCode == "22G1")
            .Join(sct.EquipmentTypes, m => m.EquipmentTypeId, t => t.EquipmentTypeId, (m, t) => t.TypeCode)
            .SingleAsync(ct);

        Assert.Equal("20GP", mapping);
    }

    [Fact]
    public async Task Global_iso_reference_data_is_readable_but_not_writable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);

        Assert.True(await db.IsoContainerCodes.CountAsync(ct) > 3000);

        // lookup.* is DENIED to both logins (01_security_foundation.sql). A tenant
        // that disagrees with ISO 6346 maps around it; it does not edit the standard.
        var code = await db.IsoContainerCodes.FirstAsync(i => i.IsoCode == "22G1", ct);
        code.DescriptionEn = "tampered";

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task Cross_tenant_writes_are_blocked_by_the_security_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDatabase.ForTenant(TestDatabase.Sct);

        db.EquipmentTypes.Add(new Infrastructure.Persistence.Entities.EquipmentType
        {
            TenantId = TestDatabase.SiamCommercial,     // not the session's tenant
            TypeCode = "XTEST",
            DescriptionEn = "Should never be inserted",
            LengthFt = 20,
            HeightClass = "STANDARD",
            IsoGroupCode = "GP",
            Teu = 1.0m,
            IsActive = true,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
    }
}
