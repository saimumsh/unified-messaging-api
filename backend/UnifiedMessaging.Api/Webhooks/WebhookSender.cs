using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnifiedMessaging.Api.Webhooks;

public record WebhookSendOutcome(bool Success, int? StatusCode, string? Error);

/// <summary>
/// Performs a single signed HTTP POST to a customer callback URL. Retries and
/// dead-lettering are handled by <see cref="WebhookRetryWorker"/>.
/// </summary>
public class WebhookSender(HttpClient http, ILogger<WebhookSender> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WebhookSendOutcome> SendAsync(
        string callbackUrl, string secret, string eventType, string deliveryId, string payloadJson, CancellationToken ct)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = Sign(secret, $"{timestamp}.{payloadJson}");

            using var req = new HttpRequestMessage(HttpMethod.Post, callbackUrl)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Unified-Event", eventType);
            req.Headers.TryAddWithoutValidation("X-Unified-Delivery", deliveryId);
            req.Headers.TryAddWithoutValidation("X-Unified-Timestamp", timestamp);
            req.Headers.TryAddWithoutValidation("X-Unified-Signature", $"sha256={signature}");

            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return new WebhookSendOutcome(true, (int)resp.StatusCode, null);

            var body = await resp.Content.ReadAsStringAsync(ct);
            return new WebhookSendOutcome(false, (int)resp.StatusCode, Truncate(body, 500));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Webhook POST to {Url} threw", callbackUrl);
            return new WebhookSendOutcome(false, null, ex.Message);
        }
    }

    public static string SerializePayload(WebhookEvent evt) => JsonSerializer.Serialize(evt, Json);

    /// <summary>Hex HMAC-SHA256, matching the WhatsApp Cloud API webhook convention.</summary>
    public static string Sign(string secret, string signedContent)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedContent));
        return Convert.ToHexStringLower(hash);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
