using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The real Gecko.Api host in memory against the local dev databases —
/// gecko_identity for login, gecko_master for codes, gecko_tos for operations.
///
/// Also a LEAK GUARD (the MasterData lesson): fixtures leave created_by NULL and
/// the API always stamps it, so a live API-written row left after the run is a
/// cleanup that failed. The run fails if the count grew.
/// </summary>
public class TosApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Gecko#Test2026";

    /// <summary>TENANT_OWNER — tos.vessel.* and tos.booking.* (incl. cancel), tenant-wide.</summary>
    public const string SctOwner = "admin@sct.co.th";

    /// <summary>
    /// Branch-scoped only: OPS_MANAGER at SCT-LCB01 and VIEWER at SCT-LKR01. Nothing
    /// tenant-wide — everything this user can do arrives in the `bpm` claim (PLAN Q11).
    /// </summary>
    public const string SctOpsLcb = "ops.lcb@sct.co.th";

    /// <summary>GATE_CLERK at SCT-LCB01: reads bookings and the schedule, manages nothing.</summary>
    public const string SctGateLcb = "gate1.lcb@sct.co.th";

    public const string SssOwner = "admin@siamshoreside.co.th";

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _tokens = new();
    private int _liveApiRowsBefore;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Identity:Lockout:LoginAttemptsPerMinutePerIp", "10000");
        // The dispatchers run in this host (TOS → Revenue → TOS for the cash window);
        // a one-second poll keeps a four-hop test to seconds.
        builder.UseSetting("Outbox:PollSeconds", "1");
        // Attachments land in a temp folder, not in the API project.
        builder.UseSetting("FileStore:Root", Path.Combine(Path.GetTempPath(), "gecko-tos-test-files"));
    }

    public async ValueTask InitializeAsync() => _liveApiRowsBefore = await LiveApiRowsAsync();

    public override async ValueTask DisposeAsync()
    {
        var after = await LiveApiRowsAsync();
        await base.DisposeAsync();
        if (after > _liveApiRowsBefore)
            throw new InvalidOperationException(
                $"Tests left {after - _liveApiRowsBefore} live API-written TOS row(s) behind (cleanup failed).");
    }

    private static async Task<int> LiveApiRowsAsync()
    {
        var total = 0;
        foreach (var tenant in new[] { TestDatabase.Sct, TestDatabase.Sss })
        {
            await using var db = TestDatabase.ForTenant(tenant);
            total += await db.VesselCalls.CountAsync(c => c.CreatedBy != null)
                   + await db.VesselCallLines.CountAsync(l => l.CreatedBy != null)
                   + await db.VesselCallCutoffs.CountAsync(c => c.CreatedBy != null)
                   + await db.Bookings.CountAsync(b => b.CreatedBy != null)
                   + await db.EquipmentRequirements.CountAsync(r => r.CreatedBy != null)
                   + await db.BookingContainers.CountAsync(x => x.CreatedBy != null)
                   + await db.MovementPlans.CountAsync(m => m.CreatedBy != null)
                   + await db.ContainerHolds.CountAsync(h => h.CreatedBy != null)
                   + await db.CutoffExceptions.CountAsync(e => e.CreatedBy != null)
                   + await db.GateTransactions.CountAsync(g => g.CreatedBy != null)
                   + await db.TruckVisits.CountAsync(v => v.CreatedBy != null)
                   + await db.ContainerVisits.CountAsync(v => v.CreatedBy != null)
                   + await db.ReeferPowerSessions.CountAsync(r => r.CreatedBy != null);
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
public sealed class TosApiCollection : ICollectionFixture<TosApiFactory>
{
    public const string Name = "tos-api";
}
