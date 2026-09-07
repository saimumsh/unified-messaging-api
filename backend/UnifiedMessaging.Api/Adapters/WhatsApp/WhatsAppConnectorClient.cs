using System.Net.Http.Json;
using UnifiedMessaging.Api.Contracts;

namespace UnifiedMessaging.Api.Adapters.WhatsApp;

/// <summary>Thin typed client over the Node/Baileys connector service.</summary>
public class WhatsAppConnectorClient(HttpClient http)
{
    public async Task ConnectAsync(Guid accountId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/accounts/{accountId}/connect", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ConnectorQrResponse?> GetQrAsync(Guid accountId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ConnectorQrResponse>($"/accounts/{accountId}/qr", ct);

    /// <summary>Raw JSON of the connector's recent-events debug buffer.</summary>
    public async Task<string> GetDebugEventsAsync(CancellationToken ct = default) =>
        await http.GetStringAsync("/debug/events", ct);

    public async Task<List<ConnectorGroup>> GetGroupsAsync(Guid accountId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/accounts/{accountId}/groups", ct);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<List<ConnectorGroup>>(ct) ?? [];

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new ConnectorException((int)resp.StatusCode, $"connector groups failed: {body}");
    }

    public Task<ConnectorSendResult> SendTextAsync(
        Guid accountId, string to, string text, string? replyTo, string[]? mentions, CancellationToken ct = default) =>
        PostSendAsync(accountId, new { to, text, replyTo, mentions }, ct);

    public Task<ConnectorSendResult> SendPollAsync(
        Guid accountId, string to, string name, string[] options, int selectableCount, CancellationToken ct = default) =>
        PostSendAsync(accountId, new { to, poll = new { name, options, selectableCount } }, ct, "/poll");

    public async Task<string> GetGroupMetadataAsync(Guid accountId, string groupJid, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/accounts/{accountId}/groups/{Uri.EscapeDataString(groupJid)}", ct);
        if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync(ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new ConnectorException((int)resp.StatusCode, $"connector group metadata failed: {body}");
    }

    public Task<ConnectorSendResult> SendReactionAsync(
        Guid accountId, string to, string targetMessageId, bool targetFromMe, string? targetParticipant,
        string emoji, CancellationToken ct = default) =>
        PostSendAsync(accountId, new
        {
            to,
            target = new { id = targetMessageId, fromMe = targetFromMe, participant = targetParticipant },
            emoji,
        }, ct, "/react");

    public Task<ConnectorSendResult> SendMediaAsync(Guid accountId, string to, OutboundMedia media, CancellationToken ct = default) =>
        PostSendAsync(accountId, new
        {
            to,
            media = new
            {
                type = media.Type,
                url = media.Url,
                dataBase64 = media.DataBase64,
                caption = media.Caption,
                fileName = media.FileName,
                mimetype = media.MimeType,
                ptt = media.Ptt,
            },
        }, ct);

    private async Task<ConnectorSendResult> PostSendAsync(Guid accountId, object body, CancellationToken ct, string path = "/send")
    {
        var resp = await http.PostAsJsonAsync($"/accounts/{accountId}{path}", body, ct);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<ConnectorSendResult>(ct) ?? new ConnectorSendResult(null, null);

        var text = await resp.Content.ReadAsStringAsync(ct);
        throw new ConnectorException((int)resp.StatusCode, $"connector send failed: {text}");
    }

    public async Task LogoutAsync(Guid accountId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/accounts/{accountId}/logout", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }
}

public record ConnectorQrResponse(string? Qr, string? Status);

public record ConnectorGroup(string Id, string? Subject, int? Size);

public record ConnectorSendResult(string? Id, string? Jid);

public class ConnectorException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
