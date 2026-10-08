namespace Gecko.Identity.Contracts;

/// <summary>
/// Names for user ids another module has stored (a cashier on a receipt, the
/// clerk on an EIR). Only the caller's tenant's users resolve — RLS decides —
/// and a user who has since been removed still has a name. An id that does not
/// resolve is simply absent. Nothing else about the user is exposed.
/// </summary>
public interface IUserDirectory
{
    Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IEnumerable<Guid> userIds, CancellationToken ct);

    /// <summary>The branches' display names (a report's heading), the caller's tenant only.</summary>
    Task<IReadOnlyDictionary<Guid, string>> BranchNamesAsync(IEnumerable<Guid> branchIds, CancellationToken ct);
}
