using UnifiedMessaging.Api.Data;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Auth;

/// <summary>
/// Per-account authentication. Callers pass the account password as the
/// <c>X-Account-Password</c> header (or HTTP Basic, username = account id).
/// </summary>
public static class AccountAuth
{
    public static string? ExtractPassword(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Account-Password", out var h) && !string.IsNullOrEmpty(h))
            return h.ToString();

        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(auth["Basic ".Length..]));
                var i = decoded.IndexOf(':');
                if (i >= 0) return decoded[(i + 1)..];
            }
            catch { /* malformed header */ }
        }
        return null;
    }

    /// <summary>
    /// Loads the account and checks the request password. Returns the account on
    /// success, or an IResult (404 / 401) to short-circuit the endpoint.
    /// </summary>
    public static async Task<(UnifiedAccount? Account, IResult? Error)> ResolveAsync(
        Guid id, HttpRequest request, AppDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.FindAsync([id], ct);
        if (account is null)
            return (null, Results.NotFound(new { error = "account not found" }));

        var password = ExtractPassword(request);
        if (password is null || !AccountPasswords.Verify(password, account.PasswordHash))
            return (null, Results.Json(new { error = "invalid account password" },
                statusCode: StatusCodes.Status401Unauthorized));

        return (account, null);
    }
}
