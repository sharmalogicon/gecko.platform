namespace Gecko.Notification.Domain.Entities.Config;

/// <summary>
/// config.recipient_rule — For each trigger, which recipient roles get notified.
/// </summary>
public class RecipientRule
{
    public Guid Id { get; set; }
    public Guid TriggerConfigId { get; set; }
    public int RecipientTypeId { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
