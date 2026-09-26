using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Tests.Api;

/// <summary>
/// Counts LIVE rows that were written through the API.
///
/// Fixture scripts leave created_by NULL; the API always stamps the caller. So in
/// the dev database a live row with created_by set can only have come from a
/// test — and a test that leaves one behind has a cleanup that silently failed.
/// That is exactly how 12 'EXP CY/CY T…' order types piled up in SCT: the
/// DELETE returned 404 (the %2F route bug) and nobody asserted on it.
///
/// The table list is read from the catalogue, so a table added later is covered
/// without touching this file.
/// </summary>
internal static class ApiRowLeakGuard
{
    public static async Task<IReadOnlyDictionary<string, int>> SnapshotAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, int>();
        foreach (var tenant in new[] { TestDatabase.Sct, TestDatabase.SiamCommercial })
        {
            await using var db = TestDatabase.ForTenant(tenant);
            await db.Database.OpenConnectionAsync(ct);   // the interceptor scopes the session here
            var connection = db.Database.GetDbConnection();

            var tables = new List<string>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = """
                    SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
                    FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                    WHERE t.temporal_type <> 1 AND s.name NOT IN ('lookup','history','dbo')
                      AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = 'created_by')
                      AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = 'deleted_at')
                    ORDER BY 1
                    """;
                await using var reader = await list.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
            }

            foreach (var table in tables)
            {
                await using DbCommand count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM {table} WHERE created_by IS NOT NULL AND deleted_at IS NULL";
                var n = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
                if (n > 0) result[$"{tenant:N}"[..8] + " " + table] = n;
            }
        }
        return result;
    }

    /// <summary>
    /// The API can only SOFT-delete (gecko_app is DENIED DELETE, on purpose), so
    /// every create-then-delete test left a deleted row behind: 273 order types and
    /// ~1,100 rows in all had piled up in SCT by 2026-09-23. This hard-deletes the
    /// rows a test wrote (created_by set) and then deleted, in the two FIXTURE
    /// tenants only — named here, so a real client's rows can never qualify.
    /// Needs an owner-level login (dev: Windows auth = sysadmin), because the
    /// application login rightly cannot do this.
    /// </summary>
    public static async Task<int> PurgeSoftDeletedAsync(CancellationToken ct)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.AdminConnection);
        await connection.OpenAsync(ct);

        await using (var system = connection.CreateCommand())
        {
            system.CommandText = "EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;";
            await system.ExecuteNonQueryAsync(ct);
        }

        var tables = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = """
                SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
                FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE t.temporal_type <> 1 AND s.name NOT IN ('lookup','history','dbo')
                  AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = 'created_by')
                  AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = 'deleted_at')
                  AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = 'tenant_id')
                ORDER BY 1
                """;
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }

        var purged = 0;
        foreach (var table in tables)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = $"""
                DELETE FROM {table}
                WHERE tenant_id IN (@sct, @other) AND created_by IS NOT NULL AND deleted_at IS NOT NULL
                """;
            delete.Parameters.AddWithValue("@sct", TestDatabase.Sct);
            delete.Parameters.AddWithValue("@other", TestDatabase.SiamCommercial);
            purged += await delete.ExecuteNonQueryAsync(ct);
        }
        return purged;
    }

    public static string? Compare(IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        var grown = after
            .Where(a => a.Value > before.GetValueOrDefault(a.Key))
            .Select(a => $"{a.Key}: {before.GetValueOrDefault(a.Key)} -> {a.Value}")
            .ToList();
        return grown.Count == 0 ? null : string.Join("; ", grown);
    }
}
