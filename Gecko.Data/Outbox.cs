using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gecko.Data;

/// <summary>One message as the dispatcher reads it. The payload is JSON the producing context wrote; nobody here interprets it.</summary>
public sealed record OutboxMessage(
    long MessageId, Guid TenantId, string AggregateType, Guid? AggregateId,
    string MessageType, string PayloadJson, Guid? CorrelationId,
    DateTimeOffset OccurredAt, int AttemptCount, int MaxAttempts);

/// <summary>
/// Something that does the work a message describes. A handler either succeeds
/// or throws: the dispatcher owns retrying, backing off and dead-lettering, so
/// a handler that swallows its own errors is a message that says it was
/// delivered when it was not.
/// </summary>
public interface IOutboxHandler
{
    bool CanHandle(string messageType);

    Task HandleAsync(OutboxMessage message, CancellationToken ct);
}

public sealed class OutboxOptions
{
    /// <summary>The SYSTEM connection string name. gecko_app may INSERT into the outbox and may not read it (11_outbox / 06_outbox).</summary>
    public string ConnectionName { get; set; } = "";

    /// <summary>Which outbox — "TOS", "IDENTITY" — used in the worker id and the logs.</summary>
    public string Context { get; set; } = "";

    public int BatchSize { get; set; } = 20;

    /// <summary>How long a claim is held. A worker that dies mid-message releases it when this expires, rather than stranding it.</summary>
    public int LeaseSeconds { get; set; } = 60;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>False leaves messages in the table — for a test that wants to inspect the queue, or a host that should not dispatch.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Drains one context's outbox (gecko_tos 06, gecko_identity 11).
///
/// The transactional half is already done in SQL: a message is written in the
/// SAME transaction as the thing it describes, so a gate event that rolls back
/// never queues a message telling the customer their box arrived. This is the
/// other half — claiming with a lease, handing to handlers, and recording the
/// outcome — and it is deliberately thin, because the interesting rules
/// (READPAST so two workers never take one message, the 5/10/20/40/80 second
/// backoff, the dead letter that is kept rather than deleted) live in the
/// procedures where they can be tested without a host.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    OutboxOptions settings,
    ILogger<OutboxDispatcher> log) : BackgroundService
{
    private readonly string _workerId = $"{settings.Context}:{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ConnectionName))
        {
            log.LogInformation("Outbox dispatcher for {Context} is off.", settings.Context);
            return;
        }

        log.LogInformation("Outbox dispatcher for {Context} started as {Worker}.", settings.Context, _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                handled = await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The queue is unreachable, not the message unhandleable: wait and try again.
                log.LogError(ex, "Outbox dispatcher for {Context} could not reach the queue.", settings.Context);
            }

            // A busy queue is drained without pausing; an idle one is polled.
            if (handled == 0)
                await Task.Delay(settings.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<int> DrainAsync(CancellationToken ct)
    {
        string connectionString;
        using (var scope = scopes.CreateScope())
            connectionString = scope.ServiceProvider.GetRequiredService<IOutboxConnectionSource>().ConnectionString(settings.ConnectionName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await SetSystemContextAsync(connection, ct);

        var claimed = await ClaimAsync(connection, settings, ct);

        foreach (var message in claimed)
        {
            try
            {
                // One scope per message, acting as the message's tenant: a handler
                // that writes (Revenue's projection, TOS's coupon) uses its module's
                // ordinary RLS-bound connection and can only touch that tenant's rows.
                // No handler needs, or gets, system context.
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<OutboxTenantScope>().TenantId = message.TenantId;
                var matched = scope.ServiceProvider.GetServices<IOutboxHandler>().Where(h => h.CanHandle(message.MessageType)).ToList();
                foreach (var handler in matched)
                    await handler.HandleAsync(message, ct);

                // No handler is not a failure: a context may publish events nobody
                // subscribes to yet. Retrying forever would dead-letter them all.
                // Information, not Debug: "nobody is listening" is the kind of quiet
                // that hides a whole feature being unwired.
                log.LogInformation("Outbox {MessageType} ({MessageId}) went to {Handlers} handler(s).",
                    message.MessageType, message.MessageId, matched.Count);

                await CompleteAsync(connection, message.MessageId, ct);
            }
            catch (Exception ex)
            {
                var terminal = message.AttemptCount >= message.MaxAttempts;
                log.Log(terminal ? LogLevel.Error : LogLevel.Warning, ex,
                    "Outbox {MessageType} ({MessageId}) failed on attempt {Attempt} of {Max}{Terminal}.",
                    message.MessageType, message.MessageId, message.AttemptCount, message.MaxAttempts,
                    terminal ? " — dead-lettered" : "");
                await FailAsync(connection, message.MessageId, Truncate(ex.Message), ct);
            }
        }

        return claimed.Count;
    }

    private static async Task SetSystemContextAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sp_set_session_context @key = N'IsSystemContext', @value = 1;";
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<OutboxMessage>> ClaimAsync(SqlConnection connection, OutboxOptions settings, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "outbox.usp_claim_messages";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@worker_id", _workerId);
        command.Parameters.AddWithValue("@batch_size", settings.BatchSize);
        command.Parameters.AddWithValue("@lease_seconds", settings.LeaseSeconds);

        var messages = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            messages.Add(new OutboxMessage(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.GetDateTimeOffset(7), reader.GetInt32(8), reader.GetInt32(9)));

        return messages;
    }

    private static async Task CompleteAsync(SqlConnection connection, long messageId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "outbox.usp_complete_message";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@message_id", messageId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task FailAsync(SqlConnection connection, long messageId, string error, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "outbox.usp_fail_message";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@message_id", messageId);
        command.Parameters.AddWithValue("@error", error);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Truncate(string value) => value.Length <= 2000 ? value : value[..2000];
}

/// <summary>
/// The tenant a message belongs to, for the scope that handles it.
/// <see cref="ClaimsTenantContext"/> falls back to it when there is no signed-in
/// caller, so a handler's DbContext is scoped exactly as a request's would be.
/// </summary>
public sealed class OutboxTenantScope
{
    public Guid? TenantId { get; set; }
}

/// <summary>Where the dispatcher gets its connection string. The host owns configuration; Gecko.Data does not read it.</summary>
public interface IOutboxConnectionSource
{
    string ConnectionString(string name);
}

public static class OutboxRegistration
{
    /// <summary>
    /// Starts a dispatcher for one context's outbox. The connection MUST be the
    /// gecko_system one: gecko_app may queue a message and may not read the queue
    /// (06_outbox / 11_outbox), which is what stops a request draining its own
    /// events inside its own transaction.
    /// </summary>
    /// <remarks>
    /// Callable once per context. Each dispatcher gets its OWN options instance:
    /// an earlier version configured one shared IOptions and used AddHostedService,
    /// which silently ignores a second registration of the same type — a second
    /// context would either never start or rewrite the first one's connection.
    /// </remarks>
    public static IServiceCollection AddOutboxDispatcher(
        this IServiceCollection services, string context, string connectionName, Action<OutboxOptions>? configure = null)
    {
        services.TryAddSingleton<IOutboxConnectionSource, ConfigurationOutboxConnections>();
        services.TryAddScoped<OutboxTenantScope>();
        var options = new OutboxOptions { Context = context, ConnectionName = connectionName };
        configure?.Invoke(options);
        services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<OutboxDispatcher>(sp, options));
        return services;
    }
}

internal sealed class ConfigurationOutboxConnections(IConfiguration configuration) : IOutboxConnectionSource
{
    public string ConnectionString(string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"ConnectionStrings:{name} is not set. The outbox dispatcher needs the gecko_system connection: " +
                $"dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\" --project Gecko.Api");
}
