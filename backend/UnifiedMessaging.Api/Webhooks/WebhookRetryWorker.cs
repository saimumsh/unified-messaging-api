using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Data;

namespace UnifiedMessaging.Api.Webhooks;

/// <summary>
/// Polls for due webhook deliveries, retries them with exponential backoff, and
/// dead-letters (status = "failed") once <see cref="RetryPolicy.MaxAttempts"/> is hit.
/// </summary>
public class WebhookRetryWorker(IServiceScopeFactory scopeFactory, ILogger<WebhookRetryWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessDueDeliveriesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Webhook retry sweep failed");
            }
        }
    }

    private async Task ProcessDueDeliveriesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<WebhookSender>();

        var now = DateTime.UtcNow;
        var due = await db.WebhookDeliveries
            .Where(d => d.Status == "pending" && (d.NextRetryAt == null || d.NextRetryAt <= now))
            .OrderBy(d => d.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        // Load the subscriptions referenced by this batch in one query.
        var subIds = due.Select(d => d.SubscriptionId).Distinct().ToList();
        var subs = await db.WebhookSubscriptions
            .Where(s => subIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        foreach (var delivery in due)
        {
            if (!subs.TryGetValue(delivery.SubscriptionId, out var sub) || !sub.IsActive)
            {
                delivery.Status = "failed";
                delivery.LastError = "subscription missing or inactive";
                continue;
            }

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
            else if (delivery.Attempts >= RetryPolicy.MaxAttempts)
            {
                delivery.Status = "failed"; // dead letter
                logger.LogError(
                    "Webhook delivery {DeliveryId} dead-lettered after {Attempts} attempts",
                    delivery.Id, delivery.Attempts);
            }
            else
            {
                delivery.NextRetryAt = DateTime.UtcNow + RetryPolicy.Backoff(delivery.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
