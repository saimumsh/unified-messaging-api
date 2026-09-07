using System.Text.Json.Serialization;

namespace UnifiedMessaging.Api.Models;

/// <summary>
/// A single connected inbox on some provider (one WhatsApp number, one Gmail
/// mailbox, one LinkedIn profile, ...). Every adapter maps into this shape.
/// </summary>
public class UnifiedAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>"whatsapp", "telegram", "gmail", "linkedin", ...</summary>
    public string Provider { get; set; } = default!;

    /// <summary>
    /// The account identifier as the provider knows it. For WhatsApp this is the
    /// phone JID once connected (e.g. "34123456789@s.whatsapp.net").
    /// </summary>
    public string? ExternalAccountId { get; set; }

    /// <summary>Human label shown to the customer ("Support line", "Sales").</summary>
    public string? DisplayName { get; set; }

    /// <summary>"pending", "waiting_for_scan", "connected", "disconnected", "needs_reauth".</summary>
    public string Status { get; set; } = AccountStatus.Pending;

    /// <summary>PBKDF2 hash of the account password. Never serialized.</summary>
    [JsonIgnore] public string PasswordHash { get; set; } = default!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public List<UnifiedMessage> Messages { get; set; } = new();
    [JsonIgnore] public List<WebhookSubscription> WebhookSubscriptions { get; set; } = new();
}

public static class AccountStatus
{
    public const string Pending = "pending";
    public const string WaitingForScan = "waiting_for_scan";
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";
    public const string NeedsReauth = "needs_reauth";
}

public static class MessageDirection
{
    public const string Inbound = "inbound";
    public const string Outbound = "outbound";
}

public class UnifiedMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AccountId { get; set; }
    [JsonIgnore] public UnifiedAccount? Account { get; set; }

    public string Provider { get; set; } = default!;

    /// <summary>Conversation/thread id (WhatsApp: the remote JID).</summary>
    public string ChatId { get; set; } = default!;

    /// <summary>Who sent it, provider-native id.</summary>
    public string SenderId { get; set; } = default!;

    /// <summary>The provider's own message id, used for idempotency / dedupe.</summary>
    public string? ProviderMessageId { get; set; }

    public string? Text { get; set; }

    public List<UnifiedAttachment> Attachments { get; set; } = new();

    /// <summary>"inbound" / "outbound".</summary>
    public string Direction { get; set; } = default!;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // ---- Reply / quote ----
    /// <summary>Provider id of the message this one replies to (null if not a reply).</summary>
    public string? ReplyToMessageId { get; set; }
    /// <summary>Short preview of the quoted message.</summary>
    public string? ReplyToText { get; set; }
    /// <summary>Sender of the quoted message.</summary>
    public string? ReplyToSenderId { get; set; }

    // ---- Mentions ----
    /// <summary>JIDs mentioned in this message.</summary>
    public List<string> Mentions { get; set; } = new();

    // ---- Poll (when this message IS a poll) ----
    public string? PollName { get; set; }
    public List<string> PollOptions { get; set; } = new();

    /// <summary>Untouched provider payload, kept for debugging / replay.</summary>
    public string? RawJson { get; set; }
}

public class UnifiedAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }

    /// <summary>"image", "video", "audio", "document", "sticker".</summary>
    public string Type { get; set; } = default!;
    public string? Url { get; set; }
    public string? MimeType { get; set; }
    public string? FileName { get; set; }
    public long? SizeBytes { get; set; }
}

public class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AccountId { get; set; }
    [JsonIgnore] public UnifiedAccount? Account { get; set; }

    public string CallbackUrl { get; set; } = default!;

    /// <summary>Shared secret used to HMAC-sign each delivery.</summary>
    public string Secret { get; set; } = default!;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One attempt log / dead-letter record for a webhook delivery to a customer.
/// </summary>
public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SubscriptionId { get; set; }
    public Guid? MessageId { get; set; }

    public string EventType { get; set; } = default!;
    public string Payload { get; set; } = default!;

    /// <summary>"pending", "delivered", "failed" (exhausted retries -> dead letter).</summary>
    public string Status { get; set; } = "pending";

    public int Attempts { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
}

/// <summary>
/// A chat/thread the account is in. One row per (account, chatId), kept fresh as
/// messages flow so customers get an inbox view without scanning all messages.
/// </summary>
public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AccountId { get; set; }
    [JsonIgnore] public UnifiedAccount? Account { get; set; }

    public string Provider { get; set; } = default!;
    public string ChatId { get; set; } = default!;

    public bool IsGroup { get; set; }
    /// <summary>Group subject, or contact push-name for 1:1 (best effort).</summary>
    public string? Name { get; set; }

    public DateTime? LastMessageAt { get; set; }
    public string? LastMessagePreview { get; set; }
    public string? LastMessageDirection { get; set; }
    public int UnreadCount { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
