using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Adapters;
using UnifiedMessaging.Api.Adapters.WhatsApp;
using UnifiedMessaging.Api.Auth;
using UnifiedMessaging.Api.Contracts;
using UnifiedMessaging.Api.Data;
using UnifiedMessaging.Api.Models;
using UnifiedMessaging.Api.Realtime;
using UnifiedMessaging.Api.Webhooks;

namespace UnifiedMessaging.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        // Create an account. The password is required and set once; every
        // /api/accounts/{id}/* call must then send it as X-Account-Password.
        group.MapPost("/", async (CreateAccountRequest req, ProviderResolver resolver, AppDbContext db) =>
        {
            if (!resolver.Supports(req.Provider))
                return Results.BadRequest(new { error = $"Unknown provider '{req.Provider}'." });
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
                return Results.BadRequest(new { error = "password is required (min 6 chars)" });

            var account = new UnifiedAccount
            {
                Provider = req.Provider,
                DisplayName = req.DisplayName,
                PasswordHash = AccountPasswords.Hash(req.Password),
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            return Results.Created($"/api/accounts/{account.Id}", account);
        });

        group.MapGet("/", async (AppDbContext db) =>
            await db.Accounts.AsNoTracking().OrderByDescending(a => a.CreatedAt).ToListAsync());

        // Requires the password (returns the account, or verifies credentials).
        group.MapGet("/{id:guid}", async (Guid id, HttpRequest http, AppDbContext db, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            return error ?? Results.Ok(account);
        });

        // Begin the connect flow (QR for WhatsApp, OAuth redirect for others).
        group.MapPost("/{id:guid}/connect", async (
            Guid id, HttpRequest http, ProviderResolver resolver, AppDbContext db,
            AccountRealtimeNotifier notifier, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;

            var adapter = resolver.Get(account!.Provider);

            ConnectResult result;
            try
            {
                result = await adapter.ConnectAccountAsync(
                    new ConnectRequest(account.Id, account.Provider, account.DisplayName), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or ConnectorException)
            {
                return Results.Json(new { error = $"connector unavailable: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            account.Status = result.Status;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            if (result.QrImageDataUrl is not null)
                await notifier.QrAsync(account.Id, result.QrImageDataUrl);
            await notifier.StatusAsync(account.Id, result.Status);

            return Results.Ok(result);
        });

        group.MapGet("/{id:guid}/connect", async (
            Guid id, HttpRequest http, ProviderResolver resolver, AppDbContext db, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;

            var result = await resolver.Get(account!.Provider).GetConnectStatusAsync(id, ct);
            return Results.Ok(result);
        });

        // End the WhatsApp session and wipe stored credentials (next connect = fresh QR).
        group.MapPost("/{id:guid}/logout", async (
            Guid id, HttpRequest http, ProviderResolver resolver, AppDbContext db,
            WhatsAppConnectorClient connector, AccountRealtimeNotifier notifier, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;

            try { await connector.LogoutAsync(id, ct); }
            catch (Exception ex) when (ex is HttpRequestException or ConnectorException) { /* connector down; still clear local state */ }

            account!.Status = AccountStatus.Disconnected;
            account.ExternalAccountId = null;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await notifier.StatusAsync(id, AccountStatus.Disconnected);

            return Results.Ok(new { status = "logged_out" });
        });

        // Send an outbound message through this account.
        group.MapPost("/{id:guid}/messages", async (
            Guid id, HttpRequest http, SendMessageRequest req, ProviderResolver resolver, AppDbContext db,
            CustomerWebhookDispatcher dispatcher, ConversationService conversations, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;
            if (account!.Status != AccountStatus.Connected)
                return Results.Conflict(new { error = $"Account is '{account.Status}', not connected." });

            if (string.IsNullOrEmpty(req.Text) && req.Media is null)
                return Results.BadRequest(new { error = "provide 'text' or 'media'" });
            if (req.Media is { } m && string.IsNullOrEmpty(m.Url) && string.IsNullOrEmpty(m.DataBase64))
                return Results.BadRequest(new { error = "media needs 'url' or 'dataBase64'" });

            var adapter = resolver.Get(account.Provider);

            UnifiedMessage message;
            try
            {
                message = req.Media is { } media
                    ? await adapter.SendMediaAsync(id.ToString(), req.ChatId, media, ct)
                    : await adapter.SendMessageAsync(id.ToString(), req.ChatId, req.Text!,
                        req.ReplyToMessageId, req.Mentions, ct);
            }
            catch (ConnectorException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = $"connector unreachable: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return await PersistOutboundAsync(message, db, dispatcher, conversations, id, ct);
        });

        // Create a poll.
        group.MapPost("/{id:guid}/polls", async (
            Guid id, HttpRequest http, SendPollRequest req, ProviderResolver resolver, AppDbContext db,
            CustomerWebhookDispatcher dispatcher, ConversationService conversations, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;
            if (account!.Status != AccountStatus.Connected)
                return Results.Conflict(new { error = $"Account is '{account.Status}', not connected." });
            if (string.IsNullOrWhiteSpace(req.Name) || req.Options is not { Length: >= 2 })
                return Results.BadRequest(new { error = "poll needs a name and at least 2 options" });

            var adapter = resolver.Get(account.Provider);
            UnifiedMessage message;
            try
            {
                message = await adapter.SendPollAsync(
                    id.ToString(), req.ChatId, req.Name, req.Options,
                    Math.Clamp(req.SelectableCount, 1, req.Options.Length), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or ConnectorException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }

            return await PersistOutboundAsync(message, db, dispatcher, conversations, id, ct);
        });

        // React to a message with an emoji (Emoji = "" removes the reaction).
        group.MapPost("/{id:guid}/reactions", async (
            Guid id, HttpRequest http, SendReactionRequest req, AppDbContext db,
            WhatsAppConnectorClient connector, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;
            if (account!.Status != AccountStatus.Connected)
                return Results.Conflict(new { error = $"Account is '{account.Status}', not connected." });

            var jid = req.ChatId.Contains('@') ? req.ChatId : $"{req.ChatId.TrimStart('+')}@s.whatsapp.net";
            try
            {
                await connector.SendReactionAsync(
                    id, jid, req.TargetMessageId, req.TargetFromMe, req.TargetParticipant, req.Emoji, ct);
                return Results.Accepted($"/api/accounts/{id}/messages",
                    new { status = req.Emoji == "" ? "reaction_removed" : "reaction_sent" });
            }
            catch (Exception ex) when (ex is HttpRequestException or ConnectorException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // List WhatsApp groups the account is in (so you can grab a group JID to message).
        group.MapGet("/{id:guid}/groups", async (
            Guid id, HttpRequest http, AppDbContext db, WhatsAppConnectorClient connector, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;
            if (account!.Provider != "whatsapp")
                return Results.BadRequest(new { error = "group listing is WhatsApp-only" });

            try
            {
                return Results.Ok(await connector.GetGroupsAsync(id, ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or ConnectorException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Full metadata + participant list for one group.
        group.MapGet("/{id:guid}/groups/{groupJid}", async (
            Guid id, string groupJid, HttpRequest http, AppDbContext db,
            WhatsAppConnectorClient connector, CancellationToken ct) =>
        {
            var (account, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;
            if (account!.Provider != "whatsapp")
                return Results.BadRequest(new { error = "groups are WhatsApp-only" });

            try
            {
                return Results.Content(await connector.GetGroupMetadataAsync(id, groupJid, ct), "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or ConnectorException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Inbox view: one row per chat, newest first.
        group.MapGet("/{id:guid}/conversations", async (
            Guid id, HttpRequest http, AppDbContext db, CancellationToken ct, int take = 100) =>
        {
            var (_, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;

            var rows = await db.Conversations.AsNoTracking()
                .Where(c => c.AccountId == id)
                .OrderByDescending(c => c.LastMessageAt ?? c.UpdatedAt)
                .Take(Math.Clamp(take, 1, 500))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // Mark a conversation read (reset unread count).
        group.MapPost("/{id:guid}/conversations/{chatId}/read", async (
            Guid id, string chatId, HttpRequest http, AppDbContext db, CancellationToken ct) =>
        {
            var (_, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;

            var updated = await db.Conversations
                .Where(c => c.AccountId == id && c.ChatId == chatId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UnreadCount, 0), ct);
            return updated > 0 ? Results.NoContent() : Results.NotFound();
        });

        // Optional filters: ?direction=inbound|outbound  ?chatId=<jid>  ?take=
        group.MapGet("/{id:guid}/messages", async (
            Guid id, HttpRequest http, AppDbContext db, CancellationToken ct,
            string? direction = null, string? chatId = null, int take = 50) =>
        {
            var (_, error) = await AccountAuth.ResolveAsync(id, http, db, ct);
            if (error is not null) return error;

            var q = db.Messages.AsNoTracking().Include(m => m.Attachments).Where(m => m.AccountId == id);
            if (direction is MessageDirection.Inbound or MessageDirection.Outbound)
                q = q.Where(m => m.Direction == direction);
            if (!string.IsNullOrWhiteSpace(chatId))
                q = q.Where(m => m.ChatId == chatId);

            var rows = await q.OrderByDescending(m => m.Timestamp)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // WhatsApp connector debug buffer (not per-account; dev troubleshooting only).
        app.MapGet("/api/whatsapp/debug/events", async (WhatsAppConnectorClient connector, CancellationToken ct) =>
        {
            try
            {
                return Results.Content(await connector.GetDebugEventsAsync(ct), "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or ConnectorException)
            {
                return Results.Json(new { error = $"connector unreachable: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithTags("Debug");
    }

    private static async Task<IResult> PersistOutboundAsync(
        UnifiedMessage message, AppDbContext db, CustomerWebhookDispatcher dispatcher,
        ConversationService conversations, Guid accountId, CancellationToken ct)
    {
        var already = message.ProviderMessageId is not null && await db.Messages.AnyAsync(
            m => m.AccountId == message.AccountId && m.ProviderMessageId == message.ProviderMessageId, ct);
        if (already)
            return Results.Accepted($"/api/accounts/{accountId}/messages", new { status = "sent", deduped = true });

        db.Messages.Add(message);
        await conversations.ApplyMessageAsync(message, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Results.Accepted($"/api/accounts/{accountId}/messages", new { status = "sent", deduped = true });
        }

        await dispatcher.DispatchAsync(WebhookEvent.Message(message), message.Id, ct);
        return Results.Accepted($"/api/accounts/{accountId}/messages/{message.Id}", message);
    }
}
