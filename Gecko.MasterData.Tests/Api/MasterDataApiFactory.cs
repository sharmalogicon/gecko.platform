using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// The real Gecko.Api host in memory, against the local dev gecko_master and
/// gecko_identity. Connection strings and the signing key come from the API's
/// user-secrets (Development environment), exactly as when running from Visual
/// Studio — so these tests exercise the real RLS policy and the real DENYs.
///
/// One instance per test run: logins are cached per user so the suite does not
/// burn the login rate limit or add hundreds of auth_event rows.
/// </summary>
public class MasterDataApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private IReadOnlyDictionary<string, int> _liveApiRowsBefore = new Dictionary<string, int>();

    public async ValueTask InitializeAsync() =>
        _liveApiRowsBefore = await ApiRowLeakGuard.SnapshotAsync(CancellationToken.None);

    /// <summary>
    /// Fails the run if the suite left API-written rows behind. Runs once, after
    /// every test in the collection, so a leak is reported even when the test
    /// that caused it passed.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        var after = await ApiRowLeakGuard.SnapshotAsync(CancellationToken.None);
        await ApiRowLeakGuard.PurgeSoftDeletedAsync(CancellationToken.None);
        await base.DisposeAsync();
        if (ApiRowLeakGuard.Compare(_liveApiRowsBefore, after) is { } leaked)
            throw new InvalidOperationException($"Tests left live API-written rows behind (cleanup failed): {leaked}");
    }

    public const string Password = "Gecko#Test2026";

    /// <summary>TENANT_OWNER, tenant-wide — holds all 12 mdm.* permissions.</summary>
    public const string SctAdmin = "admin@sct.co.th";

    /// <summary>EDI_COORDINATOR, tenant-wide — holds mdm.equipment.view but NOT mdm.equipment.manage.</summary>
    public const string SctEdi = "edi@sct.co.th";

    /// <summary>OPS_MANAGER and VIEWER, BRANCH-scoped only — so no tenant-wide permission at all.</summary>
    public const string SctOpsLcb = "ops.lcb@sct.co.th";

    /// <summary>TENANT_OWNER of another tenant, whose equipment vocabulary is deliberately different.</summary>
    public const string SiamCommercialAdmin = "admin@siamcommercial.co.th";

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _tokens = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Identity:Lockout:LoginAttemptsPerMinutePerIp", "10000");
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
public sealed class MasterDataApiCollection : ICollectionFixture<MasterDataApiFactory>
{
    public const string Name = "master-data-api";
}
