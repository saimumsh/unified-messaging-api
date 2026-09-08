using System.Net.Http.Json;
using UnifiedMessaging.Api.Adapters.WhatsApp; // ConnectorException, ConnectorSendResult

namespace UnifiedMessaging.Api.Adapters.LinkedIn;

/// <summary>Thin typed client over the Node LinkedIn (Voyager) connector service.</summary>
public class LinkedInConnectorClient(HttpClient http)
{
    /// <summary>
    /// Submit the session cookie + ban-avoidance identity and start the realtime
    /// stream. <paramref name="credentials"/> keys: <c>li_at</c>, <c>jsessionid</c>,
    /// <c>proxyUrl</c> (required), <c>userAgent</c> (optional).
    /// </summary>
    public async Task<LinkedInStatusResponse?> ConnectAsync(
        Guid accountId, IDictionary<string, string>? credentials, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            $"/accounts/{accountId}/connect", credentials ?? new Dictionary<string, string>(), ct);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<LinkedInStatusResponse>(ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new ConnectorException((int)resp.StatusCode, $"linkedin connect failed: {body}");
    }

    public async Task<LinkedInStatusResponse?> GetStatusAsync(Guid accountId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LinkedInStatusResponse>($"/accounts/{accountId}/qr", ct);

    /// <summary>Submit an emailed / SMS / authenticator code for a pending login checkpoint.</summary>
    public async Task<LinkedInStatusResponse?> SubmitChallengeAsync(Guid accountId, string code, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"/accounts/{accountId}/challenge", new { code }, ct);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<LinkedInStatusResponse>(ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new ConnectorException((int)resp.StatusCode, $"linkedin challenge failed: {body}");
    }

    /// <summary><paramref name="to"/> is a thread id (<c>2-…==</c>) or a member URN.</summary>
    public async Task<ConnectorSendResult> SendTextAsync(
        Guid accountId, string to, string text, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"/accounts/{accountId}/send", new { to, text }, ct);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<ConnectorSendResult>(ct) ?? new ConnectorSendResult(null, null);

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new ConnectorException((int)resp.StatusCode, $"linkedin send failed: {body}");
    }

    public async Task LogoutAsync(Guid accountId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/accounts/{accountId}/logout", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }
}

/// <summary>Body the LinkedIn connector returns from /connect, /challenge and /qr.</summary>
public record LinkedInStatusResponse(string? Status, string? Qr, string? ExternalAccountId, bool Checkpoint = false);
