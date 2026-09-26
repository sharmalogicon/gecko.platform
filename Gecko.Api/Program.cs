using Gecko.Api;
using Gecko.Identity;
using Gecko.MasterData;
using Gecko.Data;
using Gecko.Notification;
using Gecko.Revenue;
using Gecko.Tos;

var builder = WebApplication.CreateBuilder(args);

// Runs as a Windows service when started by the SCM (sc.exe / New-Service, DEPLOY_VM.md 6.3);
// does nothing from a console, Visual Studio, the tests or Linux.
builder.Host.UseWindowsService();

builder.Services
    .AddIdentityModule(builder.Configuration)
    .AddMasterDataModule(builder.Configuration)
    .AddRevenueModule(builder.Configuration)
    .AddTosModule(builder.Configuration)
    .AddNotificationModule(builder.Configuration);

// The outbox drains here, in the host, not inside a request: a gate event queues
// its message in the same transaction as the EIR, and this worker delivers it
// afterwards (ADR-006). gecko_app cannot read the queue — hence the SYSTEM
// connection. Set Outbox:Enabled=false to leave the queue alone (the tests do).
builder.Services.AddOutboxDispatcher("TOS", "TosSystem", options =>
{
    options.Enabled = builder.Configuration.GetValue("Outbox:Enabled", true);
    options.PollInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Outbox:PollSeconds", 5));
});

// Revenue's outbox: GateCouponIssued, the cash window's word to the barrier
// (PLAN_BILLING §4.2). Same dispatcher, its own queue and its own worker.
builder.Services.AddOutboxDispatcher("REVENUE", "RevenueSystem", options =>
{
    options.Enabled = builder.Configuration.GetValue("Outbox:Enabled", true);
    options.PollInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Outbox:PollSeconds", 5));
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GeckoExceptionHandler>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

var app = builder.Build();

// Production chain: Caddy/IIS -> next start -> Gecko.Api, every hop on loopback.
// Without this every caller is 127.0.0.1 and all users share ONE login rate-limit
// partition (Identity, LoginAttemptsPerMinutePerIp). Must run before the rate
// limiter and auth. X-Forwarded-For is honoured ONLY from loopback peers: a header
// sent by anything else is ignored, so it cannot be used to dodge the limit.
app.UseForwardedHeaders(GeckoForwardedHeaders.Options(app.Configuration));

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "GECKO Platform v1");
        options.EnablePersistAuthorization();   // survive a page refresh while testing
    });
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().WithTags("Health");

app.MapIdentityEndpoints();
app.MapMasterDataEndpoints();
app.MapRevenueEndpoints();
app.MapTosEndpoints();
app.MapNotificationEndpoints();

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
