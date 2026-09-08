using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Adapters;
using UnifiedMessaging.Api.Adapters.WhatsApp;
using UnifiedMessaging.Api.Contracts;
using UnifiedMessaging.Api.Data;
using UnifiedMessaging.Api.Models;
using UnifiedMessaging.Api.Realtime;
using UnifiedMessaging.Api.Webhooks;

namespace UnifiedMessaging.Api.Endpoints;

/// <summary>
/// Endpoints the Node/Baileys connector calls. In production these sit behind a
/// shared-secret check (see <c>ConnectorAuth</c> in Program.cs).
/// </summary>
public static class WebhookIngestEndpoints
{
    public static void MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Connector");

        // ---- Inbound messages from any provider connector ----
        group.MapPost("/webhooks/{provider}", async (
            string provider, HttpRequest request, ProviderResolver resolver, AppDbContext db,
            CustomerWebhookDispatcher dispatcher, ConversationService conversations,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("WebhookIngest");

            if (!resolver.Supports(provider))
                return Results.BadRequest(new { error = $"Unknown provider '{provider}'." });

            var adapter = resolver.Get(provider);

            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            var payload = doc.RootElement;

            if (adapter is WhatsAppAdapter wa)
            {
                // Emoji reactions — events, not messages.
                if (WhatsAppAdapter.IsReaction(payload))
                {
                    var reaction = wa.ParseReaction(payload);
                    if (reaction is null) return Results.Ok(new { ignored = true });
                    await dispatcher.DispatchAsync(WebhookEvent.Reaction(reaction), ct: ct);
                    return Results.Ok(new { reaction = true });
                }

                // Group membership / metadata changes.
                if (WhatsAppAdapter.IsGroupEvent(payload))
                {
                    var g = wa.ParseGroupEvent(payload);
                    if (g is null) return Results.Ok(new { ignored = true });
                    if (g.Subject is not null)
                        await conversations.SetGroupNameAsync(g.AccountId, g.GroupJid, g.Subject, ct);
                    await db.SaveChangesAsync(ct);
                    await dispatcher.DispatchAsync(WebhookEvent.Group(g), ct: ct);
                    return Results.Ok(new { groupEvent = g.Type });
                }

                // Poll votes (decrypted + aggregated by the connector).
                if (WhatsAppAdapter.IsPollVote(payload))
                {
                    var v = wa.ParsePollVote(payload);
                    if (v is null) return Results.Ok(new { ignored = true });
                    await dispatcher.DispatchAsync(WebhookEvent.PollVote(v), ct: ct);
                    return Results.Ok(new { pollVote = true });
                }
            }

            UnifiedMessage? message;
            try
            {
                message = adapter.ParseIncoming(payload);
            }
            catch (Exception ex)
            {
                // Never 500 the connector over one weird payload — log the raw and move on.
                logger.LogError(ex, "Failed to parse inbound {Provider} payload: {Raw}",
                    provider, payload.GetRawText());
                return Results.Ok(new { ignored = true, parseError = ex.Message });
            }
            if (message is null) return Results.Ok(new { ignored = true });

            // Idempotency: the connector may redeliver. Unique index on
            // (AccountId, ProviderMessageId) is the backstop; check first to avoid noise.
            if (message.ProviderMessageId is not null)
            {
                var exists = await db.Messages.AnyAsync(
                    m => m.AccountId == message.AccountId && m.ProviderMessageId == message.ProviderMessageId, ct);
                if (exists)
                {
                    logger.LogDebug("Duplicate message {Id} ignored", message.ProviderMessageId);
                    return Results.Ok(new { duplicate = true });
                }
            }

            db.Messages.Add(message);
            await conversations.ApplyMessageAsync(message, ct);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) // lost the race with a concurrent insert of the same message
            {
                logger.LogDebug("Duplicate message {Id} ignored (unique index)", message.ProviderMessageId);
                return Results.Ok(new { duplicate = true });
            }

            await dispatcher.DispatchAsync(WebhookEvent.Message(message), message.Id, ct);
            return Results.Ok(new { id = message.Id });
        });

        // ---- Connection status updates from any provider connector ----
        // (kept back-compatible: the WhatsApp connector still posts /internal/whatsapp/status)
        group.MapPost("/internal/{provider}/status", async (
            string provider, ConnectorStatusUpdate update, AppDbContext db, AccountRealtimeNotifier notifier,
            CustomerWebhookDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!Guid.TryParse(update.AccountId, out var accountId)) return Results.BadRequest();

            var account = await db.Accounts.FindAsync([accountId], ct);
            if (account is null) return Results.NotFound();

            var mapped = update.Status switch
            {
                "connected" => AccountStatus.Connected,
                "waiting_for_scan" => AccountStatus.WaitingForScan,
                "waiting_for_credentials" => AccountStatus.WaitingForCredentials,
                "disconnected" => AccountStatus.Disconnected,
                "logged_out" => AccountStatus.NeedsReauth,
                _ => account.Status,
            };

            account.Status = mapped;
            if (update.ExternalAccountId is not null) account.ExternalAccountId = update.ExternalAccountId;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await notifier.StatusAsync(accountId, mapped);

            if (mapped is AccountStatus.NeedsReauth)
                await dispatcher.DispatchAsync(WebhookEvent.AccountStatus(account, "account.needs_reauth"), ct: ct);
            else if (mapped is AccountStatus.Connected)
                await dispatcher.DispatchAsync(WebhookEvent.AccountStatus(account, "account.connected"), ct: ct);

            return Results.Ok();
        });
    }
}
