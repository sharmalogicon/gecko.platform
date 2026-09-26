namespace Gecko.Notification.Domain.Entities.Config;

/// <summary>
/// config.trigger_channel_map — For each trigger, which channels + templates fire.
/// Links trigger -> channel -> template per locale.
/// </summary>
public class TriggerChannelMap
{
    public Guid Id { get; set; }
    public Guid TriggerConfigId { get; set; }
    public int ChannelId { get; set; }
    public Guid? TemplateId { get; set; }
    public string Locale { get; set; } = null!;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
