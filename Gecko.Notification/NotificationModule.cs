using Gecko.Data;
using Gecko.Notification.Application;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gecko.Notification;

/// <summary>
/// The module's only entry point from the host. Gecko.Api calls Add + Map and
/// knows nothing else about what is inside.
/// </summary>
public static class NotificationModule
{
    public static IServiceCollection AddNotificationModule(this IServiceCollection services, IConfiguration configuration)
    {
        // How Notification hears about the rest of the platform: the outbox, not
        // a call. A gate event that rolls back was never queued, and a customer
        // is never told about a box that did not move (ADR-006).
        services.AddSingleton<INotificationSender, LoggingNotificationSender>();
        services.AddScoped<IOutboxHandler, GateEventHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        return endpoints;
    }
}
