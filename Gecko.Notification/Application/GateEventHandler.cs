using System.Text.Json;
using Gecko.Data;
using Gecko.Notification.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Gecko.Notification.Application;

/// <summary>
/// What actually goes out — one rendered message for one recipient on one channel.
/// </summary>
public sealed record OutboundMessage(
    Guid TenantId, string ChannelCode, string EventType, string RecipientRef,
    string Subject, string Body, Guid? CorrelationId, string IdempotencyKey);

/// <summary>
/// The wire. LINE, e-mail and SMS are adapters behind this; nothing above it
/// knows which one ran.
/// </summary>
public interface INotificationSender
{
    Task SendAsync(OutboundMessage message, CancellationToken ct);
}

/// <summary>
/// The default adapter until a LINE channel token exists: it renders the message
/// and writes it to the log.
///
/// This is not a stub that pretends — it is the honest state of the channel. The
/// gate event, the recipient, the rendered Thai/English text and the idempotency
/// key are all real; only the HTTP call to LINE is missing, and swapping it in is
/// a token plus one class. Sending to a real customer from a dev fixture would be
/// the actual mistake.
/// </summary>
public sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> log) : INotificationSender
{
    public Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        log.LogInformation(
            "[{Channel}] to {Recipient} ({Event}) key={Key}\n{Subject}\n{Body}",
            message.ChannelCode, message.RecipientRef, message.EventType, message.IdempotencyKey,
            message.Subject, message.Body);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Turns a gate event into the message the customer reads (ADR-006: Notification
/// is a module, and it learns about the gate the same way any other subscriber
/// would — from the event, never by reading gecko_tos).
///
/// The payload carries everything needed, which is why this handler has no
/// database of its own yet. Templates, per-tenant wording, opt-outs and the
/// delivery log belong in Notification's own store when it is built; until then
/// the text lives here, and the comment says so rather than the code pretending.
///
/// PDPA: the recipient is referenced by CODE, and the rendered message names the
/// box and the order — never a person, a phone number or a licence.
/// </summary>
public sealed class GateEventHandler(INotificationSender sender, ILogger<GateEventHandler> log) : IOutboxHandler
{
    public const string GatedIn = "ContainerGatedIn";
    public const string GatedOut = "ContainerGatedOut";

    public bool CanHandle(string messageType) =>
        messageType is GatedIn or GatedOut;

    /// <summary>
    /// The producing context writes camelCase; System.Text.Json matches property
    /// names CASE-SENSITIVELY by default, so without this every field deserialises
    /// to null and the handler decides there is nobody to tell — silently, and
    /// only for real events. Found by gating a box and watching nothing arrive.
    /// </summary>
    private static readonly JsonSerializerOptions Payload = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var gate = JsonSerializer.Deserialize<GateEventPayload>(message.PayloadJson, Payload)
                   ?? throw new InvalidOperationException($"Outbox message {message.MessageId} carries no gate event.");

        // Nobody to tell is not a failure: a walk-in with no customer on the
        // booking still gates perfectly well.
        if (string.IsNullOrWhiteSpace(gate.CustomerCode))
        {
            log.LogDebug("Gate event {MessageId} has no customer to notify ({ContainerNo}).", message.MessageId, gate.ContainerNo);
            return;
        }

        var arriving = message.MessageType == GatedIn;
        var box = gate.ContainerNo ?? "";
        var pretty = box.Length == 11 ? $"{box[..4]} {box[4..10]} {box[10..]}" : box;
        var at = gate.TransactionAt.ToLocalTime().ToString("dd MMM HH:mm");

        var body = arriving
            ? $"{pretty} arrived at the depot at {at} on order {gate.OrderNo}. EIR {gate.EirNo}."
              + (gate.IsLate ? " It was accepted after the cut-off, by agreement." : "")
            : $"{pretty} left the depot at {at} on order {gate.OrderNo}. EIR {gate.EirNo}."
              + (gate.BookingContainerCompleted ? " That completes the order for this box." : "");

        await sender.SendAsync(new OutboundMessage(
            TenantId: message.TenantId,
            ChannelCode: ChannelCode.From("LINE").Value,
            EventType: arriving ? "CONTAINER_GATED_IN" : "CONTAINER_GATED_OUT",
            RecipientRef: gate.CustomerCode,
            Subject: arriving ? $"{pretty} is in" : $"{pretty} is out",
            Body: body,
            CorrelationId: message.CorrelationId,
            // The message id IS the idempotency key: a lease that expires mid-send
            // makes the dispatcher hand the same message to a second worker, and
            // the customer must not be told twice.
            IdempotencyKey: $"outbox:{message.MessageId}"), ct);
    }

    /// <summary>The shape gecko_tos writes (GateEndpoints.QueueAsync). Read, never interpreted beyond this.</summary>
    private sealed record GateEventPayload(
        Guid GateTransactionId, string? EirNo, Guid BranchId, string? ContainerNo,
        string? Direction, string? MovementCode, string? FullEmpty, Guid BookingId,
        string? OrderNo, string? LineCode, string? CustomerCode,
        DateTimeOffset TransactionAt, bool IsLate, bool BookingContainerCompleted);
}
