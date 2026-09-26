using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Tests.Api;

/// <summary>
/// The real Gecko.Api host in memory against the local dev databases —
/// gecko_identity for login, gecko_master for codes, gecko_revenue for tariffs.
/// Logins are cached per user.
///
/// Also a LEAK GUARD (the MasterData lesson): fixture scripts leave created_by
/// NULL and the API always stamps it, so a live API-written row left behind
/// after the run is a cleanup that failed. The run fails if the count grew.
/// </summary>
public class RevenueApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Gecko#Test2026";

    /// <summary>TENANT_OWNER — view, manage, APPROVE, import.</summary>
    public const string SctOwner = "admin@sct.co.th";

    /// <summary>ACCOUNTS, tenant-wide — view, manage, import, but NOT approve.</summary>
    public const string SctAccounts = "accounts@sct.co.th";

    /// <summary>OPS_MANAGER, branch-scoped only — no tenant-wide revenue.* at all.</summary>
    public const string SctOpsLcb = "ops.lcb@sct.co.th";

    /// <summary>TENANT_OWNER of the second fixture tenant (SSS).</summary>
    public const string SssOwner = "admin@siamshoreside.co.th";

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _tokens = new();
    private int _liveApiRowsBefore;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Identity:Lockout:LoginAttemptsPerMinutePerIp", "10000");
    }

    public async ValueTask InitializeAsync() => _liveApiRowsBefore = await LiveApiRowsAsync();

    public override async ValueTask DisposeAsync()
    {
        var after = await LiveApiRowsAsync();
        await base.DisposeAsync();
        if (after > _liveApiRowsBefore)
            throw new InvalidOperationException(
                $"Tests left {after - _liveApiRowsBefore} live API-written tariff row(s) behind (cleanup failed).");
    }

    private static async Task<int> LiveApiRowsAsync()
    {
        var total = 0;
        foreach (var tenant in new[] { TestDatabase.Sct, TestDatabase.Sss })
        {
            await using var db = TestDatabase.ForTenant(tenant);
            total += await db.Schedules.CountAsync(s => s.CreatedBy != null)
                   + await db.TosRates.CountAsync(r => r.CreatedBy != null)
                   + await db.RateTiers.CountAsync(t => t.CreatedBy != null)
                   + await db.RateConditions.CountAsync(c => c.CreatedBy != null)
                   + await db.FreeTimeRules.CountAsync(f => f.CreatedBy != null)
                   + await db.TemplateExports.CountAsync(e => e.CreatedBy != null)
                   + await db.ImportBatches.CountAsync(b => b.CreatedBy != null)
                   + await db.ImportRows.CountAsync(r => r.CreatedBy != null);
        }
        return total;
    }

    public async Task<HttpClient> ClientForAsync(string email)
    {
        var token = await _tokens.GetOrAdd(email, e => new Lazy<Task<string>>(() => LoginAsync(e))).Value;
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> LoginAsync(string email)
    {
        using var anonymous = CreateClient();
        var response = await anonymous.PostAsJsonAsync("/auth/login", new { email, password = Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginBody>())!.AccessToken;
    }

    private sealed record LoginBody(string AccessToken);
}

[CollectionDefinition(Name)]
public sealed class RevenueApiCollection : ICollectionFixture<RevenueApiFactory>
{
    public const string Name = "revenue-api";
}
