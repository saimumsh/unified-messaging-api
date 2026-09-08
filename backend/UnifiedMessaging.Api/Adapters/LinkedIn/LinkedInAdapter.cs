using System.Text.Json;
using UnifiedMessaging.Api.Adapters.WhatsApp; // ConnectorException (shared)
using UnifiedMessaging.Api.Contracts;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Adapters.LinkedIn;

/// <summary>
/// .NET-side adapter for LinkedIn. Delegates the raw Voyager protocol work to the
/// Node connector and normalizes its realtime payloads into <see cref="UnifiedMessage"/>.
///
/// Unofficial channel: personal session cookie + internal Voyager endpoints. See
/// LINKEDIN_PLAN.md. No polls; reactions / media / groups are phase 2.
/// </summary>
public class LinkedInAdapter(LinkedInConnectorClient connector, ILogger<LinkedInAdapter> logger)
    : IMessagingProviderAdapter
{
    public string ProviderName => "linkedin";

    public async Task<ConnectResult> ConnectAccountAsync(ConnectRequest request, CancellationToken ct = default)
    {
        var creds = request.Credentials;

        // No credentials yet -> tell the caller to collect the cookie + proxy
        // (or username/password). The connector still resolves a managed proxy on
        // its own if PROXY_PROVIDER is webshare, so an empty bag can still connect
        // from a stored session — but a first-time account needs input.
        if (creds is not { Count: > 0 })
            return new ConnectResult(request.AccountId, AccountStatus.WaitingForCredentials);

        // Re-submitting after a login checkpoint: { challengeCode | code }.
        if (creds.TryGetValue("challengeCode", out var code) || creds.TryGetValue("code", out code))
        {
            var ch = await connector.SubmitChallengeAsync(request.AccountId, code, ct);
            return new ConnectResult(request.AccountId, MapStatus(ch?.Status), Checkpoint: ch?.Checkpoint ?? false);
        }

        var resp = await connector.ConnectAsync(request.AccountId, creds, ct);
        return new ConnectResult(request.AccountId, MapStatus(resp?.Status), Checkpoint: resp?.Checkpoint ?? false);
    }

    public async Task<ConnectResult> GetConnectStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        var resp = await connector.GetStatusAsync(accountId, ct);
        return new ConnectResult(accountId, MapStatus(resp?.Status), Checkpoint: resp?.Checkpoint ?? false);
    }

    private static string MapStatus(string? connectorStatus) => connectorStatus switch
    {
        "connected" => AccountStatus.Connected,
        "waiting_for_credentials" => AccountStatus.WaitingForCredentials,
        "disconnected" => AccountStatus.Disconnected,
        "logged_out" => AccountStatus.NeedsReauth,
        _ => AccountStatus.Pending,
    };

    public async Task LogoutAsync(Guid accountId, CancellationToken ct = default)
    {
        try
        {
            await connector.LogoutAsync(accountId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or ConnectorException)
        {
            logger.LogWarning(ex, "LinkedIn connector unreachable during logout for {AccountId}", accountId);
        }
    }

    public async Task<UnifiedMessage> SendMessageAsync(
        string accountId, string chatId, string text,
        string? replyToMessageId = null, string[]? mentions = null, CancellationToken ct = default)
    {
        var sent = await connector.SendTextAsync(Guid.Parse(accountId), chatId, text, ct);
        return new UnifiedMessage
        {
            AccountId = Guid.Parse(accountId),
            Provider = ProviderName,
            ChatId = chatId,
            SenderId = "me",
            // Set so the realtime echo of our own message dedupes against the unique index.
            ProviderMessageId = sent.Id,
            Text = text,
            Attachments = [],
            Direction = MessageDirection.Outbound,
            Timestamp = DateTime.UtcNow,
        };
    }

    public Task<UnifiedMessage> SendMediaAsync(string accountId, string chatId, OutboundMedia media, CancellationToken ct = default) =>
        throw new NotSupportedException("LinkedIn media send is not implemented yet (phase 2).");

    public Task<UnifiedMessage> SendPollAsync(
        string accountId, string chatId, string name, string[] options, int selectableCount, CancellationToken ct = default) =>
        throw new NotSupportedException("LinkedIn has no polls.");

    /// <summary>
    /// Normalize one connector realtime payload: <c>{ accountId, raw }</c> where
    /// <c>raw</c> is <c>{ threadId, eventUrn, from, fromMe, body:{text}, attachments, createdAt }</c>.
    /// </summary>
    public UnifiedMessage? ParseIncoming(JsonElement payload)
    {
        if (!payload.TryGetProperty("accountId", out var accEl) || accEl.GetString() is not { } accountId)
            return null;
        if (!Guid.TryParse(accountId, out var accountGuid))
            return null;
        if (!payload.TryGetProperty("raw", out var raw) || raw.ValueKind != JsonValueKind.Object)
            return null;

        var threadId = GetString(raw, "threadId");
        if (threadId is null)
        {
            logger.LogDebug("Skipping LinkedIn payload with no threadId");
            return null;
        }

        var fromMe = raw.TryGetProperty("fromMe", out var fm) && fm.ValueKind == JsonValueKind.True;

        string? text = null;
        if (raw.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
            text = GetString(body, "text");
        text ??= GetString(raw, "subject"); // InMail / connection-request note

        var attachments = ParseAttachments(raw);

        if (string.IsNullOrEmpty(text) && attachments.Count == 0)
        {
            logger.LogDebug("Skipping non-content LinkedIn event {Urn}", GetString(raw, "eventUrn"));
            return null;
        }

        var timestamp = TryGetInt64(raw, "createdAt") is { } ms and > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : DateTime.UtcNow;

        return new UnifiedMessage
        {
            AccountId = accountGuid,
            Provider = ProviderName,
            ChatId = threadId,
            SenderId = fromMe ? "me" : GetString(raw, "from") ?? "unknown",
            ProviderMessageId = GetString(raw, "eventUrn"),
            Text = text,
            Attachments = attachments,
            Direction = fromMe ? MessageDirection.Outbound : MessageDirection.Inbound,
            Timestamp = timestamp,
            RawJson = raw.GetRawText(),
        };
    }

    private static List<UnifiedAttachment> ParseAttachments(JsonElement raw)
    {
        var result = new List<UnifiedAttachment>();
        if (!raw.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var a in arr.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            result.Add(new UnifiedAttachment
            {
                Type = GetString(a, "type") ?? "document",
                Url = GetString(a, "url"),
                MimeType = GetString(a, "mimeType") ?? GetString(a, "mediaType"),
                FileName = GetString(a, "fileName") ?? GetString(a, "name"),
                SizeBytes = TryGetInt64(a, "size") ?? TryGetInt64(a, "byteSize"),
            });
        }
        return result;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(prop, out var p) &&
        p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static long? TryGetInt64(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(p.GetString(), out var n) => n,
            _ => null,
        };
    }
}
