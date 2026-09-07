using UnifiedMessaging.Api.Webhooks;

namespace UnifiedMessaging.Api.Tests;

public class WebhookSignatureTests
{
    [Fact]
    public void Sign_is_stable_and_hex_sha256()
    {
        var sig = WebhookSender.Sign("shhh", "1757082000.{\"a\":1}");

        Assert.Equal(64, sig.Length);
        Assert.Matches("^[0-9a-f]+$", sig);
        Assert.Equal(sig, WebhookSender.Sign("shhh", "1757082000.{\"a\":1}"));
    }

    [Fact]
    public void Sign_changes_with_secret_and_payload()
    {
        var a = WebhookSender.Sign("secret-1", "1.x");
        var b = WebhookSender.Sign("secret-2", "1.x");
        var c = WebhookSender.Sign("secret-1", "1.y");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(1, 45, 90)]     // ~60s + jitter
    [InlineData(3, 200, 300)]   // ~240s + jitter
    [InlineData(20, 21600, 26000)] // capped at 6h + jitter
    public void Backoff_grows_and_caps(int attempt, double minSeconds, double maxSeconds)
    {
        var delay = RetryPolicy.Backoff(attempt).TotalSeconds;
        Assert.InRange(delay, minSeconds, maxSeconds);
    }
}
