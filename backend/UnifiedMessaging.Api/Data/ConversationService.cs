using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Data;

/// <summary>Keeps the per-chat <see cref="Conversation"/> row fresh as messages flow.</summary>
public class ConversationService(AppDbContext db)
{
    public async Task ApplyMessageAsync(UnifiedMessage m, CancellationToken ct)
    {
        var convo = await db.Conversations
            .FirstOrDefaultAsync(c => c.AccountId == m.AccountId && c.ChatId == m.ChatId, ct);

        if (convo is null)
        {
            convo = new Conversation
            {
                AccountId = m.AccountId,
                Provider = m.Provider,
                ChatId = m.ChatId,
                IsGroup = m.ChatId.EndsWith("@g.us"),
            };
            db.Conversations.Add(convo);
        }

        // Only advance on newer messages (out-of-order webhooks happen).
        if (convo.LastMessageAt is null || m.Timestamp >= convo.LastMessageAt)
        {
            convo.LastMessageAt = m.Timestamp;
            convo.LastMessagePreview = Preview(m);
            convo.LastMessageDirection = m.Direction;
        }

        if (m.Direction == MessageDirection.Inbound) convo.UnreadCount++;
        else convo.UnreadCount = 0; // our own reply clears it

        convo.UpdatedAt = DateTime.UtcNow;
    }

    public async Task SetGroupNameAsync(Guid accountId, string chatId, string? name, CancellationToken ct)
    {
        var convo = await db.Conversations
            .FirstOrDefaultAsync(c => c.AccountId == accountId && c.ChatId == chatId, ct);
        if (convo is null)
        {
            convo = new Conversation
            {
                AccountId = accountId, Provider = "whatsapp", ChatId = chatId, IsGroup = true,
            };
            db.Conversations.Add(convo);
        }
        convo.Name = name;
        convo.IsGroup = true;
        convo.UpdatedAt = DateTime.UtcNow;
    }

    private static string Preview(UnifiedMessage m)
    {
        if (m.PollName is not null) return $"[poll] {m.PollName}";
        if (!string.IsNullOrEmpty(m.Text)) return m.Text.Length <= 120 ? m.Text : m.Text[..120];
        var att = m.Attachments.FirstOrDefault();
        return att is not null ? $"[{att.Type}]" : "[message]";
    }
}
