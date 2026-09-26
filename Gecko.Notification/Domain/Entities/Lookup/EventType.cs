using Gecko.Notification.Domain.Enums;

namespace Gecko.Notification.Domain.Entities.Lookup;

/// <summary>
/// lookup.event_type — All valid event types across GECKO modules.
/// </summary>
public class EventType
{
    public int Id { get; set; }
    public string EventCode { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public SourceModule SourceModule { get; set; }
    public string Category { get; set; } = null!;
    public Priority DefaultPriority { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
