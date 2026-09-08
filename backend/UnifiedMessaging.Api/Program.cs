using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Adapters;
using UnifiedMessaging.Api.Adapters.LinkedIn;
using UnifiedMessaging.Api.Adapters.WhatsApp;
using UnifiedMessaging.Api.Data;
using UnifiedMessaging.Api.Endpoints;
using UnifiedMessaging.Api.Realtime;
using UnifiedMessaging.Api.Webhooks;
using UnifiedMessaging.Api.Workers;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=unified_messaging;Username=unified;Password=unified";
var connectorBaseUrl = builder.Configuration["WhatsAppConnector:BaseUrl"] ?? "http://localhost:3001";
var linkedInConnectorBaseUrl = builder.Configuration["LinkedInConnector:BaseUrl"] ?? "http://localhost:3002";
var connectorSecret = builder.Configuration["Connector:SharedSecret"] ?? "dev-connector-secret";

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);

// ---- Persistence ----
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

// ---- Realtime ----
builder.Services.AddSignalR();
builder.Services.AddSingleton<AccountRealtimeNotifier>();

// ---- Provider adapters ----
builder.Services.AddHttpClient<WhatsAppConnectorClient>(c =>
{
    c.BaseAddress = new Uri(connectorBaseUrl);
    c.DefaultRequestHeaders.Add("X-Connector-Secret", connectorSecret);
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IMessagingProviderAdapter, WhatsAppAdapter>();

builder.Services.AddHttpClient<LinkedInConnectorClient>(c =>
{
    c.BaseAddress = new Uri(linkedInConnectorBaseUrl);
    c.DefaultRequestHeaders.Add("X-Connector-Secret", connectorSecret);
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IMessagingProviderAdapter, LinkedInAdapter>();
// Later: builder.Services.AddScoped<IMessagingProviderAdapter, TelegramAdapter>(); etc.
builder.Services.AddScoped<ProviderResolver>();
builder.Services.AddScoped<UnifiedMessaging.Api.Data.ConversationService>();

// ---- Customer webhook delivery ----
builder.Services.AddHttpClient<WebhookSender>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<CustomerWebhookDispatcher>();
builder.Services.AddHostedService<WebhookRetryWorker>();
builder.Services.AddHostedService<AccountHealthWorker>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials()));

var app = builder.Build();

// ---- DB migrate on startup (fine for dev; use a migration job in prod) ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

// Shared-secret gate for the endpoints the Node connector calls.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    var isConnectorPath = path.StartsWithSegments("/api/internal")
        || (path.StartsWithSegments("/api/webhooks") && HttpMethods.IsPost(ctx.Request.Method));

    if (isConnectorPath)
    {
        var provided = ctx.Request.Headers["X-Connector-Secret"].ToString();
        if (!CryptographicEquals(provided, connectorSecret))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "bad connector secret" });
            return;
        }
    }

    await next();
});

app.MapGet("/api", () => Results.Ok(new { service = "unified-messaging-api", status = "ok" }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapAccountEndpoints();
app.MapConnectorEndpoints();
app.MapSubscriptionEndpoints();
app.MapHub<AccountHub>("/hubs/accounts");

app.Run();

static bool CryptographicEquals(string a, string b) =>
    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

public partial class Program;
