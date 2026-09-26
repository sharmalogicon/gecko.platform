namespace Gecko.Notification.Domain.Entities.Lookup;

/// <summary>
/// lookup.priority — Maps priority labels to Service Bus topics and SLA.
/// </summary>
public class PriorityLookup
{
    public int Id { get; set; }
    public string PriorityCode { get; set; } = null!;
    public byte PriorityLevel { get; set; }
    public string DisplayName { get; set; } = null!;
    public string ServiceBusTopic { get; set; } = null!;
    public int MaxDeliveryCount { get; set; }
    public int LockDurationSeconds { get; set; }
    public int MessageTtlHours { get; set; }
    public int SlaSeconds { get; set; }
    public string? Description { get; set; }
}
