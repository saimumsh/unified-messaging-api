using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Auth;
using UnifiedMessaging.Api.Contracts;
using UnifiedMessaging.Api.Data;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Endpoints;

public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Webhook subscriptions");

        group.MapPost("/accounts/{accountId:guid}/webhooks", async (
            Guid accountId, HttpRequest http, CreateSubscriptionRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var (_, error) = await AccountAuth.ResolveAsync(accountId, http, db, ct);
            if (error is not null) return error;

            var sub = new WebhookSubscription
            {
                AccountId = accountId,
                CallbackUrl = req.CallbackUrl,
                Secret = string.IsNullOrWhiteSpace(req.Secret)
                    ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))
                    : req.Secret,
            };
            db.WebhookSubscriptions.Add(sub);
            await db.SaveChangesAsync();

            // Secret is returned exactly once, on creation.
            return Results.Created($"/api/webhooks/{sub.Id}", new
            {
                sub.Id, sub.AccountId, sub.CallbackUrl, sub.Secret, sub.IsActive, sub.CreatedAt,
            });
        });

        group.MapGet("/accounts/{accountId:guid}/webhooks", async (
            Guid accountId, HttpRequest http, AppDbContext db, CancellationToken ct) =>
        {
            var (_, error) = await AccountAuth.ResolveAsync(accountId, http, db, ct);
            if (error is not null) return error;

            var rows = await db.WebhookSubscriptions.AsNoTracking()
                .Where(s => s.AccountId == accountId)
                .Select(s => new { s.Id, s.AccountId, s.CallbackUrl, s.IsActive, s.CreatedAt })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapDelete("/webhooks/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var deleted = await db.WebhookSubscriptions.Where(s => s.Id == id).ExecuteDeleteAsync();
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        // Delivery log / dead-letter inspection.
        group.MapGet("/webhooks/{id:guid}/deliveries", async (Guid id, AppDbContext db, int take = 50) =>
            await db.WebhookDeliveries.AsNoTracking()
                .Where(d => d.SubscriptionId == id)
                .OrderByDescending(d => d.CreatedAt)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync());
    }
}
