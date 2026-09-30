using Gecko.Identity.Contracts;
using Gecko.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Application.Directory;

internal sealed class UserDirectory(IdentityDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        // Removed users keep their name: a receipt from last year still has a cashier.
        return await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => ids.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
    }
}
