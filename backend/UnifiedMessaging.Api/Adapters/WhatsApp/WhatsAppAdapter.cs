using System.Text.Json;
using UnifiedMessaging.Api.Contracts;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Adapters.WhatsApp;

/// <summary>
/// .NET-side adapter for WhatsApp. Delegates the raw protocol work to the Node
/// connector and normalizes Baileys payloads into <see cref="UnifiedMessage"/>.
/// </summary>
public class WhatsAppAdapter(WhatsAppConnectorClient connector, ILogger<WhatsAppAdapter> logger)
    : IMessagingProviderAdapter
{
    public string ProviderName => "whatsapp";

    public async Task<ConnectResult> ConnectAccountAsync(ConnectRequest request, CancellationToken ct = default)
    {
        await connector.ConnectAsync(request.AccountId, ct);

        // The QR is generated asynchronously by Baileys; poll briefly. Production
        // code pushes it over SignalR instead of blocking the request (see AccountHub).
        for (var i = 0; i < 15; i++)
        {
            var qr = await connector.GetQrAsync(request.AccountId, ct);
            if (qr?.Qr is not null)
                return new ConnectResult(request.AccountId, AccountStatus.WaitingForScan, QrImageDataUrl: qr.Qr);
            if (string.Equals(qr?.Status, "connected", StringComparison.OrdinalIgnoreCase))
                return new ConnectResult(request.AccountId, AccountStatus.Connected);
            await Task.Delay(1000, ct);
        }

        return new ConnectResult(request.AccountId, AccountStatus.Pending);
    }

    public async Task<ConnectResult> GetConnectStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        var qr = await connector.GetQrAsync(accountId, ct);
        var status = qr?.Status switch
        {
            "connected" => AccountStatus.Connected,
            "waiting_for_scan" => AccountStatus.WaitingForScan,
            "disconnected" => AccountStatus.Disconnected,
            _ => AccountStatus.Pending,
        };
        return new ConnectResult(accountId, status, QrImageDataUrl: qr?.Qr);
    }

    public async Task LogoutAsync(Guid accountId, CancellationToken ct = default)
    {
        try
        {
            await connector.LogoutAsync(accountId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or ConnectorException)
        {
            logger.LogWarning(ex, "WhatsApp connector unreachable during logout for {AccountId}", accountId);
        }
    }

    public async Task<UnifiedMessage> SendMessageAsync(
        string accountId, string chatId, string text,
        string? replyToMessageId = null, string[]? mentions = null, CancellationToken ct = default)
    {
        var jid = NormalizeJid(chatId);
        var sent = await connector.SendTextAsync(Guid.Parse(accountId), jid, text, replyToMessageId, mentions, ct);
        var stub = OutboundStub(accountId, jid, sent.Id, text, attachments: null);
        stub.ReplyToMessageId = replyToMessageId;
        stub.Mentions = mentions?.ToList() ?? new();
        return stub;
    }

    public async Task<UnifiedMessage> SendPollAsync(
        string accountId, string chatId, string name, string[] options, int selectableCount, CancellationToken ct = default)
    {
        var jid = NormalizeJid(chatId);
        var sent = await connector.SendPollAsync(Guid.Parse(accountId), jid, name, options, selectableCount, ct);
        var stub = OutboundStub(accountId, jid, sent.Id, name, attachments: null);
        stub.PollName = name;
        stub.PollOptions = options.ToList();
        return stub;
    }

    public async Task<UnifiedMessage> SendMediaAsync(string accountId, string chatId, OutboundMedia media, CancellationToken ct = default)
    {
        var jid = NormalizeJid(chatId);
        var sent = await connector.SendMediaAsync(Guid.Parse(accountId), jid, media, ct);
        return OutboundStub(accountId, jid, sent.Id, media.Caption, new List<UnifiedAttachment>
        {
            new()
            {
                Type = media.Type,
                Url = media.Url,
                MimeType = media.MimeType,
                FileName = media.FileName,
            },
        });
    }

    private UnifiedMessage OutboundStub(string accountId, string jid, string? providerMessageId, string? text, List<UnifiedAttachment>? attachments) =>
        new()
        {
            AccountId = Guid.Parse(accountId),
            Provider = ProviderName,
            ChatId = jid,
            SenderId = "me",
            // Set so the Baileys `fromMe` echo of this message dedupes against the unique index.
            ProviderMessageId = providerMessageId,
            Text = text,
            Attachments = attachments ?? [],
            Direction = MessageDirection.Outbound,
            Timestamp = DateTime.UtcNow,
        };

    public UnifiedMessage? ParseIncoming(JsonElement payload)
    {
        if (!payload.TryGetProperty("accountId", out var accEl) || accEl.GetString() is not { } accountId)
            return null;
        if (!payload.TryGetProperty("raw", out var raw))
            return null;

        var message = ParseMessageEvent(new ConnectorMessageEvent(accountId, raw));
        if (message is null) return null;

        // The connector decrypts + hosts media and hands us a fetchable URL.
        if (payload.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object &&
            GetString(media, "url") is { } url)
        {
            var existing = message.Attachments.FirstOrDefault();
            var att = existing ?? new UnifiedAttachment { Type = GetString(media, "type") ?? "document" };
            att.Url = url;
            att.MimeType ??= GetString(media, "mimetype");
            att.FileName ??= GetString(media, "fileName");
            if (TryGetInt64(media, "size") is { } size) att.SizeBytes = size;
            if (existing is null) message.Attachments.Add(att);
        }

        return message;
    }

    private static bool RawHas(JsonElement payload, string key) =>
        payload.TryGetProperty("raw", out var raw) &&
        raw.ValueKind == JsonValueKind.Object &&
        raw.TryGetProperty(key, out _);

    /// <summary>True if this connector payload is an emoji reaction, not a message.</summary>
    public static bool IsReaction(JsonElement payload) => RawHas(payload, "reactionEvent");
    public static bool IsGroupEvent(JsonElement payload) => RawHas(payload, "groupEvent");
    public static bool IsPollVote(JsonElement payload) => RawHas(payload, "pollVote");

    private static Guid? AccountId(JsonElement payload) =>
        payload.TryGetProperty("accountId", out var a) && Guid.TryParse(a.GetString(), out var g) ? g : null;

    private static string[] StringArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToArray()!;
    }

    /// <summary>Normalize a connector `groupEvent` payload.</summary>
    public GroupEventInfo? ParseGroupEvent(JsonElement payload)
    {
        if (AccountId(payload) is not { } accountId) return null;
        var g = payload.GetProperty("raw").GetProperty("groupEvent");
        var jid = GetString(g, "jid");
        var type = GetString(g, "type");
        if (jid is null || type is null) return null;

        return new GroupEventInfo(
            AccountId: accountId,
            GroupJid: jid,
            Type: type,
            Participants: StringArray(g, "participants"),
            Actor: GetString(g, "author"),
            Subject: GetString(g, "subject"),
            Description: GetString(g, "description"));
    }

    /// <summary>Normalize a connector `pollVote` payload.</summary>
    public PollVoteInfo? ParsePollVote(JsonElement payload)
    {
        if (AccountId(payload) is not { } accountId) return null;
        var v = payload.GetProperty("raw").GetProperty("pollVote");
        var chatId = GetString(v, "chatId");
        var pollMsgId = GetString(v, "pollMsgId");
        var voter = GetString(v, "voter");
        if (chatId is null || pollMsgId is null || voter is null) return null;

        return new PollVoteInfo(accountId, chatId, pollMsgId, voter, StringArray(v, "selectedOptions"));
    }

    /// <summary>
    /// Normalize a reaction. <c>reactionEvent.key</c> is the message being reacted
    /// to; <c>reactionEvent.reactorKey</c> (when present) identifies who reacted.
    /// </summary>
    public ReactionInfo? ParseReaction(JsonElement payload)
    {
        if (!payload.TryGetProperty("accountId", out var accEl) ||
            !Guid.TryParse(accEl.GetString(), out var accountId))
            return null;
        if (!payload.TryGetProperty("raw", out var raw) ||
            !raw.TryGetProperty("reactionEvent", out var re) ||
            !re.TryGetProperty("key", out var key))
            return null;

        var chatId = GetString(key, "remoteJid");
        if (chatId is null) return null;

        var emoji = re.TryGetProperty("reaction", out var rx) ? GetString(rx, "text") : null;
        var onYourMessage = key.TryGetProperty("fromMe", out var fm) && fm.ValueKind == JsonValueKind.True;

        string? reactedBy = null;
        var reactedByMe = false;
        if (re.TryGetProperty("reactorKey", out var rk) && rk.ValueKind == JsonValueKind.Object)
        {
            reactedByMe = rk.TryGetProperty("fromMe", out var rfm) && rfm.ValueKind == JsonValueKind.True;
            reactedBy = reactedByMe ? "me" : GetString(rk, "participant") ?? GetString(rk, "remoteJid");
        }

        return new ReactionInfo(
            AccountId: accountId,
            ChatId: chatId,
            TargetMessageId: GetString(key, "id"),
            Emoji: string.IsNullOrEmpty(emoji) ? null : emoji,
            Removed: string.IsNullOrEmpty(emoji),
            OnYourMessage: onYourMessage,
            ReactedBy: reactedBy,
            ReactedByMe: reactedByMe);
    }

    /// <summary>Normalize one Baileys `messages.upsert` entry. Public for unit tests.</summary>
    public UnifiedMessage? ParseMessageEvent(ConnectorMessageEvent evt)
    {
        var raw = evt.Raw;

        if (!raw.TryGetProperty("key", out var key) ||
            !raw.TryGetProperty("message", out var messageRaw))
        {
            return null;
        }

        // Peel container nodes (ephemeral, view-once, captioned document, ...) in
        // case the connector didn't. Defensive — the connector normally unwraps.
        var message = Unwrap(messageRaw);

        var remoteJid = GetString(key, "remoteJid");
        if (remoteJid is null) return null;

        var fromMe = key.TryGetProperty("fromMe", out var fm) && fm.ValueKind == JsonValueKind.True;
        var providerMessageId = GetString(key, "id");

        var text = ExtractText(message);
        var attachments = ExtractAttachments(message);
        var poll = ExtractPoll(message);
        var (replyToId, replyToText, replyToSender) = ExtractQuoted(message);
        var mentions = ExtractMentions(message);

        if (text is null && attachments.Count == 0 && poll is null)
        {
            logger.LogDebug("Skipping non-content WhatsApp message {Id}", providerMessageId);
            return null;
        }

        var senderJid = fromMe
            ? "me"
            : GetString(key, "participant") ?? remoteJid; // participant is set in group chats

        // messageTimestamp is a number, a numeric string, or a { low, high } Long.
        var ts = TryGetInt64(raw, "messageTimestamp")
                 ?? (raw.TryGetProperty("messageTimestamp", out var tsObj) ? TryGetInt64(tsObj, "low") : null);
        var timestamp = ts is > 0 and < 4_102_444_800
            ? DateTimeOffset.FromUnixTimeSeconds(ts.Value).UtcDateTime
            : DateTime.UtcNow;

        return new UnifiedMessage
        {
            AccountId = Guid.Parse(evt.AccountId),
            Provider = ProviderName,
            ChatId = remoteJid,
            SenderId = senderJid,
            ProviderMessageId = providerMessageId,
            Text = text ?? poll?.Name,
            Attachments = attachments,
            Direction = fromMe ? MessageDirection.Outbound : MessageDirection.Inbound,
            Timestamp = timestamp,
            ReplyToMessageId = replyToId,
            ReplyToText = replyToText,
            ReplyToSenderId = replyToSender,
            Mentions = mentions,
            PollName = poll?.Name,
            PollOptions = poll?.Options ?? new(),
            RawJson = raw.GetRawText(),
        };
    }

    private record PollInfo(string Name, List<string> Options);

    private static PollInfo? ExtractPoll(JsonElement message)
    {
        foreach (var k in new[] { "pollCreationMessage", "pollCreationMessageV2", "pollCreationMessageV3" })
        {
            if (!message.TryGetProperty(k, out var p) || p.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(p, "name");
            var options = new List<string>();
            if (p.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                foreach (var o in opts.EnumerateArray())
                    if (GetString(o, "optionName") is { } on) options.Add(on);
            if (name is not null) return new PollInfo(name, options);
        }
        return null;
    }

    private static (string? Id, string? Text, string? Sender) ExtractQuoted(JsonElement message)
    {
        foreach (var el in ContentNodes(message))
        {
            if (!el.TryGetProperty("contextInfo", out var ctx) || ctx.ValueKind != JsonValueKind.Object) continue;
            var stanzaId = GetString(ctx, "stanzaId");
            if (stanzaId is null) continue;
            var sender = GetString(ctx, "participant");
            string? preview = null;
            if (ctx.TryGetProperty("quotedMessage", out var qm))
                preview = ExtractText(Unwrap(qm)) ?? (ExtractAttachments(Unwrap(qm)).FirstOrDefault()?.Type is { } t ? $"[{t}]" : null);
            return (stanzaId, preview, sender);
        }
        return (null, null, null);
    }

    private static List<string> ExtractMentions(JsonElement message)
    {
        var result = new List<string>();
        foreach (var el in ContentNodes(message))
        {
            if (el.TryGetProperty("contextInfo", out var ctx) &&
                ctx.TryGetProperty("mentionedJid", out var mj) && mj.ValueKind == JsonValueKind.Array)
                foreach (var j in mj.EnumerateArray())
                    if (j.GetString() is { } s) result.Add(s);
        }
        return result;
    }

    /// <summary>The message-content sub-objects that can carry a contextInfo.</summary>
    private static IEnumerable<JsonElement> ContentNodes(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) yield break;
        foreach (var prop in message.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Object)
                yield return prop.Value;
    }

    private static readonly string[] ContainerKeys =
    [
        "ephemeralMessage", "viewOnceMessage", "viewOnceMessageV2", "viewOnceMessageV2Extension",
        "documentWithCaptionMessage", "deviceSentMessage", "editedMessage",
    ];

    private static JsonElement Unwrap(JsonElement message, int depth = 0)
    {
        if (depth > 5 || message.ValueKind != JsonValueKind.Object) return message;
        foreach (var k in ContainerKeys)
        {
            if (message.TryGetProperty(k, out var wrapper) &&
                wrapper.ValueKind == JsonValueKind.Object &&
                wrapper.TryGetProperty("message", out var inner))
            {
                return Unwrap(inner, depth + 1);
            }
        }
        return message;
    }

    private static string? ExtractText(JsonElement message)
    {
        if (TryGetString(message, "conversation", out var conversation))
            return conversation;

        if (message.TryGetProperty("extendedTextMessage", out var ext) &&
            TryGetString(ext, "text", out var extText))
            return extText;

        // Captions on media
        foreach (var mediaKey in new[] { "imageMessage", "videoMessage", "documentMessage" })
        {
            if (message.TryGetProperty(mediaKey, out var media) &&
                TryGetString(media, "caption", out var caption))
                return caption;
        }

        return null;
    }

    private static List<UnifiedAttachment> ExtractAttachments(JsonElement message)
    {
        var result = new List<UnifiedAttachment>();

        var map = new (string Key, string Type)[]
        {
            ("imageMessage", "image"),
            ("videoMessage", "video"),
            ("audioMessage", "audio"),
            ("documentMessage", "document"),
            ("stickerMessage", "sticker"),
        };

        foreach (var (key, type) in map)
        {
            if (!message.TryGetProperty(key, out var media)) continue;

            result.Add(new UnifiedAttachment
            {
                Type = type,
                MimeType = TryGetString(media, "mimetype", out var mime) ? mime : null,
                FileName = TryGetString(media, "fileName", out var fn) ? fn : null,
                // Baileys sends fileLength as a string ("123456"), sometimes a number.
                SizeBytes = TryGetInt64(media, "fileLength"),
                // Media bytes must be downloaded via the connector (Baileys decrypts them);
                // the connector is expected to upload to blob storage and fill this in.
                Url = TryGetString(media, "url", out var url) ? url : null,
            });
        }

        return result;
    }

    private static string NormalizeJid(string to) =>
        to.Contains('@') ? to : $"{to.TrimStart('+')}@s.whatsapp.net";

    private static string? GetString(JsonElement el, string prop) =>
        TryGetString(el, prop, out var v) ? v : null;

    private static bool TryGetString(JsonElement el, string prop, out string? value)
    {
        value = null;
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(prop, out var p) &&
            p.ValueKind == JsonValueKind.String)
        {
            value = p.GetString();
            return value is not null;
        }
        return false;
    }

    /// <summary>
    /// Reads an integer that Baileys/protobuf-over-JSON may encode as a number
    /// OR a string (e.g. fileLength "123456"). Never throws on a type mismatch.
    /// </summary>
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
