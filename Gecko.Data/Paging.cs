using System.ComponentModel;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Data;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public static class PagingExtensions
{
    public const int MaxPageSize = 200;

    /// <summary>Clamps caller-supplied paging so ?pageSize=1000000 cannot pull a whole table.</summary>
    public static async Task<PagedResult<T>> ToPagedAsync<T>(
        this IQueryable<T> query, int? page, int? pageSize, CancellationToken ct)
    {
        var p = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 50, 1, MaxPageSize);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct);

        return new PagedResult<T>(items, p, size, total);
    }
}

/// <summary>Standard list query-string parameters.</summary>
public sealed record ListQuery(
    [property: Description("1-based page number")] int? Page = 1,
    [property: Description("Items per page, max 200")] int? PageSize = 50,
    [property: Description("Case-insensitive contains-match on code / name / email")] string? Search = null);
