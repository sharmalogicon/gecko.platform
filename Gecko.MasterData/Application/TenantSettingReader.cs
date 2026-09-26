using Gecko.MasterData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Application;

/// <summary>
/// Resolves a tenant setting the way `config.tenant_setting` was designed to be
/// read (04_config_tables.sql):
///
///     branch row  ->  tenant row  ->  lookup.setting_definition.default_value
///
/// A branch row only wins for keys whose definition says allowed_scope = BRANCH;
/// a branch row on a TENANT-scoped key is a data error the dev_01 fixture guard
/// already rejects, and is ignored here rather than silently honoured.
///
/// WHY A READER AND NOT db.TenantSettings.Where(...): the fallback is the whole
/// point. Callers that query the table directly get NULL for every setting no
/// tenant has overridden — which is most of them — and then invent their own
/// default, so the declared default in lookup.setting_definition becomes
/// decoration and two parts of the system disagree about what "free storage
/// days" means when nobody has set it.
/// </summary>
public sealed class TenantSettingReader(MasterDataDbContext db)
{
    public async Task<string?> GetAsync(string settingKey, Guid? branchId, CancellationToken ct)
    {
        var definition = await db.SettingDefinitions.AsNoTracking()
            .Where(d => d.SettingKey == settingKey && d.IsActive)
            .Select(d => new { d.AllowedScope, d.DefaultValue })
            .SingleOrDefaultAsync(ct);

        // An undeclared key is a typo in C#, not a missing tenant value. Say so
        // loudly — this is exactly what declaring keys globally is for.
        if (definition is null)
            throw new InvalidOperationException(
                $"Setting '{settingKey}' is not declared in lookup.setting_definition. " +
                "Add it in a numbered script before reading it.");

        if (branchId is not null && definition.AllowedScope == "BRANCH")
        {
            var branchValue = await db.TenantSettings.AsNoTracking()
                .Where(s => s.SettingKey == settingKey && s.BranchId == branchId)
                .Select(s => s.SettingValue)
                .SingleOrDefaultAsync(ct);
            if (branchValue is not null) return branchValue;
        }

        var tenantValue = await db.TenantSettings.AsNoTracking()
            .Where(s => s.SettingKey == settingKey && s.BranchId == null)
            .Select(s => s.SettingValue)
            .SingleOrDefaultAsync(ct);

        return tenantValue ?? definition.DefaultValue;
    }

    /// <summary>
    /// Reads a BOOL setting. The stored form is the JSON-ish 'true'/'false' that
    /// lookup.setting_definition's own parse guard enforces, so anything else is
    /// treated as the fallback rather than silently becoming false.
    /// </summary>
    public async Task<bool> GetBoolAsync(string settingKey, Guid? branchId, bool fallback, CancellationToken ct) =>
        await GetAsync(settingKey, branchId, ct) is { } value && bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    public async Task<int> GetIntAsync(string settingKey, Guid? branchId, int fallback, CancellationToken ct) =>
        await GetAsync(settingKey, branchId, ct) is { } value && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;
}

/// <summary>Setting keys this module reads, as declared in 12_seed_config_definitions.sql.</summary>
public static class MasterDataSettings
{
    /// <summary>Reject a container number whose ISO 6346 check digit does not verify.</summary>
    public const string EnforceCheckDigit = "gate.enforce_check_digit";

    /// <summary>Reject a container whose prefix is not registered to a known party.</summary>
    public const string EnforceContainerPrefix = "mdm.container_prefix_enforced";
}
