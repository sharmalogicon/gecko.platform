namespace Gecko.SharedKernel;

/// <summary>
/// Who is asking, for the lifetime of one request. Resolved from the access
/// token's claims — never from a header, query string or request body, because
/// anything the caller can type, the caller can use to pick another tenant.
/// </summary>
public interface ITenantContext
{
    /// <summary>The <c>tid</c> claim. Null for an unauthenticated request.</summary>
    Guid? TenantId { get; }

    /// <summary>The <c>sub</c> claim. Stamped into created_by / updated_by / deleted_by.</summary>
    Guid? UserId { get; }
}
