using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Data;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Webhooks;

/// <summary>
/// Fans a unified event out to every active customer subscription for the account.
/// Each fan-out is persisted as a <see cref="WebhookDelivery"/> row first, then an
/// immediate delivery is attempted; failures are picked up by the retry worker.
/// </summary>
public class CustomerWebhookDispatcher(
    AppDbContext db,
    WebhookSender sender,
    ILogger<CustomerWebhookDispatcher> logger)
{
    public async Task DispatchAsync(WebhookEvent evt, Guid? messageId = null, CancellationToken ct = default)
    {
        var subs = await db.WebhookSubscriptions
            .Where(s => s.AccountId == evt.AccountId && s.IsActive)
            .AsNoTracking()
            .ToListAsync(ct);

        if (subs.Count == 0)
        {
            logger.LogDebug("No active subscriptions for account {AccountId}", evt.AccountId);
            return;
        }

        var payload = WebhookSender.SerializePayload(evt);

        var deliveries = subs.Select(s => new WebhookDelivery
        {
            SubscriptionId = s.Id,
            MessageId = messageId,
            EventType = evt.Type,
            Payload = payload,
            Status = "pending",
        }).ToList();

        db.WebhookDeliveries.AddRange(deliveries);
        await db.SaveChangesAsync(ct);

        // Best-effort immediate delivery. Anything still pending falls to the worker.
        foreach (var (sub, delivery) in subs.Zip(deliveries))
            await TryDeliverAsync(sub, delivery, ct);

        await db.SaveChangesAsync(ct);
    }

    private async Task TryDeliverAsync(WebhookSubscription sub, WebhookDelivery delivery, CancellationToken ct)
    {
        delivery.Attempts++;
        var outcome = await sender.SendAsync(
            sub.CallbackUrl, sub.Secret, delivery.EventType, delivery.Id.ToString(), delivery.Payload, ct);

        delivery.LastStatusCode = outcome.StatusCode;
        delivery.LastError = outcome.Error;

        if (outcome.Success)
        {
            delivery.Status = "delivered";
            delivery.DeliveredAt = DateTime.UtcNow;
        }
        else
        {
            delivery.NextRetryAt = DateTime.UtcNow + RetryPolicy.Backoff(delivery.Attempts);
            logger.LogWarning(
                "Webhook delivery {DeliveryId} attempt {Attempt} failed ({Status}); next retry {Next:o}",
                delivery.Id, delivery.Attempts, outcome.StatusCode, delivery.NextRetryAt);
        }

        // db is tracking `delivery` — caller saves.
        db.WebhookDeliveries.Update(delivery);
    }
}

public static class RetryPolicy
{
    public const int MaxAttempts = 8;

    /// <summary>Exponential backoff with jitter: ~1m, 2m, 4m, 8m, ... capped at 6h.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(60 * Math.Pow(2, attempt - 1), TimeSpan.FromHours(6).TotalSeconds);
        var jitter = Random.Shared.NextDouble() * 0.2 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }
}
