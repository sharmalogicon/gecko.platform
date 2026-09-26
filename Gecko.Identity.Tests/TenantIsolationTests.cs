using Gecko.Data;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Tests;

/// <summary>
/// ADR-006 build step 1. Proves the C# side honours the isolation the SQL side
/// enforces. Runs against the local dev database; writes happen inside a
/// transaction that is never committed.
/// </summary>
public class TenantIsolationTests
{
    [Fact]
    public async Task No_tenant_throws_before_the_connection_opens()
    {
        // Unreachable server on purpose: the exception must come from the
        // interceptor, before any network attempt, not from a timeout.
        await using var db = TestDatabase.ForTenant(tenantId: null,
            connection: "Server=unreachable.invalid;Database=x;User Id=x;Password=x;Connect Timeout=1");

        await Assert.ThrowsAsync<MissingTenantContextException>(() => db.Branches.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tenant_sees_only_its_own_branches()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var sct = TestDatabase.ForTenant(TestDatabase.Sct);
        await using var sss = TestDatabase.ForTenant(TestDatabase.Sss);

        var sctBranches = await sct.Branches.ToListAsync(ct);
        var sssBranches = await sss.Branches.ToListAsync(ct);

        Assert.NotEmpty(sctBranches);
        Assert.NotEmpty(sssBranches);
        Assert.All(sctBranches, b => Assert.Equal(TestDatabase.Sct, b.TenantId));
        Assert.All(sssBranches, b => Assert.Equal(TestDatabase.Sss, b.TenantId));
    }

    [Fact]
    public async Task Tenant_cannot_load_another_tenants_row_by_id()
    {
        await using var system = TestDatabase.ForSystem();
        var sssBranchId = await system.Branches
            .Where(b => b.TenantId == TestDatabase.Sss)
            .Select(b => b.BranchId)
            .FirstAsync(TestContext.Current.CancellationToken);

        await using var sct = TestDatabase.ForTenant(TestDatabase.Sct);

        Assert.Null(await sct.Branches.FindAsync([sssBranchId], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task System_context_sees_every_tenant()
    {
        await using var system = TestDatabase.ForSystem();

        var tenants = await system.Branches.Select(b => b.TenantId).Distinct().CountAsync(TestContext.Current.CancellationToken);

        Assert.True(tenants >= 5, $"system context saw {tenants} tenants");
    }

    [Fact]
    public async Task App_login_raising_the_system_flag_sees_nothing()
    {
        // The injection scenario from 09_security_hardening.sql: gecko_app issues
        // sp_set_session_context 'IsSystemContext' = 1. The predicate requires the
        // gecko_system ROLE, so the flag must be inert.
        await using var hijacked = TestDatabase.ForSystem(connection: TestDatabase.AppConnection);

        Assert.Equal(0, await hijacked.Branches.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tenant_cannot_insert_a_row_into_another_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sct = TestDatabase.ForTenant(TestDatabase.Sct);
        await using var tx = await sct.Database.BeginTransactionAsync(ct);

        sct.Branches.Add(NewBranch(tenantId: TestDatabase.Sss, "ROGUE-01"));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => sct.SaveChangesAsync(ct));
        Assert.Contains("BLOCK predicate", ex.InnerException?.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_is_a_soft_delete_and_hides_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sct = TestDatabase.ForTenant(TestDatabase.Sct);
        await using var tx = await sct.Database.BeginTransactionAsync(ct);

        var branch = NewBranch(TestDatabase.Sct, "TEST-DEL01");
        sct.Branches.Add(branch);
        await sct.SaveChangesAsync(ct);

        sct.Branches.Remove(branch);
        await sct.SaveChangesAsync(ct);   // DELETE is DENIED — this only passes if it became an UPDATE
        sct.ChangeTracker.Clear();

        Assert.False(await sct.Branches.AnyAsync(b => b.BranchId == branch.BranchId, ct));
        var raw = await sct.Branches.IgnoreQueryFilters().SingleAsync(b => b.BranchId == branch.BranchId, ct);
        Assert.NotNull(raw.DeletedAt);
    }

    // Regression guard: EF 10 sets the sentinel of `bool HasDefaultValue(true)` to true,
    // so false IS sent. Older EF versions omitted it and the DB default wrote 1.
    [Fact]
    public async Task False_is_saved_as_false_despite_the_column_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sct = TestDatabase.ForTenant(TestDatabase.Sct);
        await using var tx = await sct.Database.BeginTransactionAsync(ct);

        var branch = NewBranch(TestDatabase.Sct, "TEST-OFF01");
        branch.IsActive = false;
        sct.Branches.Add(branch);
        await sct.SaveChangesAsync(ct);
        sct.ChangeTracker.Clear();

        Assert.False(await sct.Branches.Where(b => b.BranchId == branch.BranchId).Select(b => b.IsActive).SingleAsync(ct));
    }

    private static Branch NewBranch(Guid tenantId, string code) => new()
    {
        TenantId = tenantId,
        BranchCode = code,
        DisplayName = "Test branch " + code,
        BranchType = "DEPOT",
        CountryCode = "TH",
        IsActive = true,
    };
}
