namespace Gecko.Notification.Domain.Entities.Lookup;

/// <summary>
/// lookup.recipient_type — Standard recipient roles across all modules.
/// </summary>
public class RecipientType
{
    public int Id { get; set; }
    public string RecipientCode { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
}
