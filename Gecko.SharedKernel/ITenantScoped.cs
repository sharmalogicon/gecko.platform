namespace Gecko.SharedKernel;

/// <summary>
/// Marker interface for entities that are scoped by tenant.
/// Used by EF Core interceptor to set SESSION_CONTEXT for RLS.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
