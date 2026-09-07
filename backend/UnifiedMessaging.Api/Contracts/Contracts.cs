using System.Text.Json;

namespace UnifiedMessaging.Api.Contracts;

/// <summary>Request to begin connecting an account for a given provider.</summary>
public record ConnectRequest(Guid AccountId, string Provider, string? DisplayName = null);

/// <summary>
/// Result of a connect call. For QR providers <see cref="QrImageDataUrl"/> is set;
/// for OAuth providers <see cref="RedirectUrl"/> is set instead.
/// </summary>
public record ConnectResult(
    Guid AccountId,
    string Status,
    string? QrImageDataUrl = null,
    string? RedirectUrl = null);

/// <summary>Send text (<paramref name="Text"/>) or a media file (<paramref name="Media"/>).</summary>
public record SendMessageRequest(
    string ChatId,
    string? Text = null,
    OutboundMedia? Media = null,
    string? ReplyToMessageId = null,
    string[]? Mentions = null);

/// <summary>Create a poll in a chat.</summary>
public record SendPollRequest(
    string ChatId,
    string Name,
    string[] Options,
    int SelectableCount = 1);

/// <summary>React to a message. <paramref name="Emoji"/> = "" removes the reaction.</summary>
public record SendReactionRequest(
    string ChatId,
    string TargetMessageId,
    string Emoji,
    bool TargetFromMe = false,
    string? TargetParticipant = null);

/// <summary>
/// <paramref name="Type"/> ∈ image | video | audio | document. Provide exactly one
/// of <paramref name="Url"/> (connector-fetchable) or <paramref name="DataBase64"/> (raw bytes).
/// </summary>
public record OutboundMedia(
    string Type,
    string? Url = null,
    string? DataBase64 = null,
    string? Caption = null,
    string? FileName = null,
    string? MimeType = null,
    bool Ptt = false);

public record CreateAccountRequest(string Provider, string Password, string? DisplayName = null);

public record CreateSubscriptionRequest(string CallbackUrl, string? Secret = null);

// ---- Payloads exchanged with the Node/Baileys connector ----

/// <summary>Body the connector POSTs to /api/internal/whatsapp/status.</summary>
public record ConnectorStatusUpdate(
    string AccountId,
    string Status,
    string? ExternalAccountId = null);

/// <summary>Body the connector POSTs to /api/webhooks/whatsapp for each message.</summary>
public record ConnectorMessageEvent(string AccountId, JsonElement Raw);

/// <summary>Normalized group membership / metadata change.</summary>
public record GroupEventInfo(
    Guid AccountId,
    string GroupJid,
    string Type,               // add | remove | promote | demote | subject | description | joined
    string[] Participants,
    string? Actor,
    string? Subject,
    string? Description);

/// <summary>Normalized poll vote (decrypted + aggregated by the connector).</summary>
public record PollVoteInfo(
    Guid AccountId,
    string ChatId,
    string PollMessageId,
    string VoterId,
    string[] SelectedOptions);

/// <summary>A normalized emoji reaction on a message.</summary>
public record ReactionInfo(
    Guid AccountId,
    string ChatId,
    string? TargetMessageId,
    string? Emoji,
    bool Removed,
    bool OnYourMessage,
    string? ReactedBy = null,
    bool ReactedByMe = false);
