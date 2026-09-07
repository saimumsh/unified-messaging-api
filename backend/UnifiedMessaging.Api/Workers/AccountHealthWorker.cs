using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Data;
using UnifiedMessaging.Api.Models;
using UnifiedMessaging.Api.Realtime;
using UnifiedMessaging.Api.Webhooks;

namespace UnifiedMessaging.Api.Workers;

/// <summary>
/// Watches for accounts that have been "disconnected" longer than the grace
/// window and escalates them to "needs_reauth", firing an account.needs_reauth
/// webhook so the customer's UI can prompt the end user to rescan.
/// </summary>
public class AccountHealthWorker(IServiceScopeFactory scopeFactory, ILogger<AccountHealthWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Account health sweep failed");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CustomerWebhookDispatcher>();
        var notifier = scope.ServiceProvider.GetRequiredService<AccountRealtimeNotifier>();

        var cutoff = DateTime.UtcNow - DisconnectGrace;
        var stale = await db.Accounts
            .Where(a => a.Status == AccountStatus.Disconnected && a.UpdatedAt < cutoff)
            .ToListAsync(ct);

        foreach (var account in stale)
        {
            account.Status = AccountStatus.NeedsReauth;
            account.UpdatedAt = DateTime.UtcNow;
            logger.LogWarning("Account {AccountId} ({Provider}) escalated to needs_reauth", account.Id, account.Provider);

            await notifier.StatusAsync(account.Id, AccountStatus.NeedsReauth);
            await dispatcher.DispatchAsync(WebhookEvent.AccountStatus(account, "account.needs_reauth"), ct: ct);
        }

        if (stale.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
