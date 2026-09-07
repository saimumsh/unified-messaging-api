using UnifiedMessaging.Api.Contracts;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Webhooks;

/// <summary>
/// The single, provider-agnostic envelope every customer webhook receives.
/// Same shape whether the underlying channel is WhatsApp, Telegram or Gmail.
/// </summary>
public record WebhookEvent(
    string Id,
    string Type,          // "message.received", "message.sent", "account.needs_reauth", ...
    string Provider,
    Guid AccountId,
    DateTime OccurredAt,
    object Data)
{
    private static string NewId() => $"evt_{Guid.NewGuid():N}";

    public static WebhookEvent Message(UnifiedMessage m) => new(
        Id: NewId(),
        Type: m.Direction == MessageDirection.Inbound ? "message.received" : "message.sent",
        Provider: m.Provider,
        AccountId: m.AccountId,
        OccurredAt: DateTime.UtcNow,
        Data: new MessagePayload(
            Id: m.Id,
            ChatId: m.ChatId,
            SenderId: m.SenderId,
            Text: m.Text,
            Direction: m.Direction,
            Timestamp: m.Timestamp,
            Attachments: m.Attachments
                .Select(a => new AttachmentPayload(a.Type, a.Url, a.MimeType, a.FileName, a.SizeBytes)).ToList(),
            ReplyTo: m.ReplyToMessageId is null ? null
                : new ReplyPayload(m.ReplyToMessageId, m.ReplyToText, m.ReplyToSenderId),
            Mentions: m.Mentions,
            Poll: m.PollName is null ? null : new PollPayload(m.PollName, m.PollOptions)));

    public static WebhookEvent Reaction(ReactionInfo r) => new(
        Id: NewId(),
        Type: r.Removed ? "message.reaction_removed" : "message.reaction_added",
        Provider: "whatsapp",
        AccountId: r.AccountId,
        OccurredAt: DateTime.UtcNow,
        Data: new ReactionEventPayload(r.ChatId, r.TargetMessageId, r.Emoji, r.OnYourMessage, r.ReactedBy, r.ReactedByMe));

    public static WebhookEvent Group(GroupEventInfo g) => new(
        Id: NewId(),
        Type: g.Type switch
        {
            "add" => "group.participants_added",
            "remove" => "group.participants_removed",
            "promote" => "group.participant_promoted",
            "demote" => "group.participant_demoted",
            "subject" => "group.subject_updated",
            "description" => "group.description_updated",
            "joined" => "group.joined",
            _ => "group.updated",
        },
        Provider: "whatsapp",
        AccountId: g.AccountId,
        OccurredAt: DateTime.UtcNow,
        Data: new GroupEventPayload(g.GroupJid, g.Participants, g.Actor, g.Subject, g.Description));

    public static WebhookEvent PollVote(PollVoteInfo v) => new(
        Id: NewId(),
        Type: "poll.vote",
        Provider: "whatsapp",
        AccountId: v.AccountId,
        OccurredAt: DateTime.UtcNow,
        Data: new PollVotePayload(v.ChatId, v.PollMessageId, v.VoterId, v.SelectedOptions));

    public static WebhookEvent AccountStatus(UnifiedAccount a, string type) => new(
        Id: NewId(),
        Type: type,
        Provider: a.Provider,
        AccountId: a.Id,
        OccurredAt: DateTime.UtcNow,
        Data: new AccountPayload(a.Id, a.Provider, a.Status, a.DisplayName));
}

public record MessagePayload(
    Guid Id,
    string ChatId,
    string SenderId,
    string? Text,
    string Direction,
    DateTime Timestamp,
    List<AttachmentPayload> Attachments,
    ReplyPayload? ReplyTo,
    List<string> Mentions,
    PollPayload? Poll);

public record AttachmentPayload(string Type, string? Url, string? MimeType, string? FileName, long? SizeBytes);
public record ReplyPayload(string MessageId, string? Text, string? SenderId);
public record PollPayload(string Name, List<string> Options);

public record AccountPayload(Guid Id, string Provider, string Status, string? DisplayName);

public record ReactionEventPayload(
    string ChatId, string? TargetMessageId, string? Emoji, bool OnYourMessage,
    string? ReactedBy, bool ReactedByMe);

public record GroupEventPayload(
    string GroupJid, string[] Participants, string? Actor, string? Subject, string? Description);

public record PollVotePayload(string ChatId, string PollMessageId, string VoterId, string[] SelectedOptions);
