using System.Text.Json;
using UnifiedMessaging.Api.Contracts;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Adapters;

/// <summary>
/// One implementation per channel. Everything provider-specific lives behind this
/// interface; the rest of the system only ever sees <see cref="UnifiedMessage"/>.
/// </summary>
public interface IMessagingProviderAdapter
{
    string ProviderName { get; }

    /// <summary>
    /// Kick off a connection. Returns a QR image data URL (WhatsApp/LinkedIn) or
    /// an OAuth redirect URL (Gmail/Telegram), depending on the provider.
    /// </summary>
    Task<ConnectResult> ConnectAccountAsync(ConnectRequest request, CancellationToken ct = default);

    /// <summary>Fetch the current QR (if the connect flow is still waiting for a scan).</summary>
    Task<ConnectResult> GetConnectStatusAsync(Guid accountId, CancellationToken ct = default);

    Task<UnifiedMessage> SendMessageAsync(
        string accountId, string chatId, string text,
        string? replyToMessageId = null, string[]? mentions = null, CancellationToken ct = default);

    Task<UnifiedMessage> SendMediaAsync(string accountId, string chatId, OutboundMedia media, CancellationToken ct = default);

    Task<UnifiedMessage> SendPollAsync(
        string accountId, string chatId, string name, string[] options, int selectableCount, CancellationToken ct = default);

    /// <summary>
    /// Turn a raw provider connector payload (<c>{ accountId, raw }</c>) into a
    /// normalized message. Returns null for events that don't map to a message
    /// (reactions, receipts, presence...).
    /// </summary>
    UnifiedMessage? ParseIncoming(JsonElement payload);
}
