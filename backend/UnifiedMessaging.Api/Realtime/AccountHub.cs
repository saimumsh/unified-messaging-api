using Microsoft.AspNetCore.SignalR;

namespace UnifiedMessaging.Api.Realtime;

/// <summary>
/// Frontend connects here and calls <c>Subscribe(accountId)</c> to watch a
/// connect flow. The backend pushes "qr" and "status" messages as they happen,
/// so the QR screen advances without polling.
/// </summary>
public class AccountHub : Hub
{
    public Task Subscribe(string accountId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, Group(accountId));

    public Task Unsubscribe(string accountId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(accountId));

    public static string Group(string accountId) => $"account:{accountId}";
}

public class AccountRealtimeNotifier(IHubContext<AccountHub> hub)
{
    public Task QrAsync(Guid accountId, string qrDataUrl) =>
        hub.Clients.Group(AccountHub.Group(accountId.ToString()))
            .SendAsync("qr", new { accountId, qr = qrDataUrl });

    public Task StatusAsync(Guid accountId, string status) =>
        hub.Clients.Group(AccountHub.Group(accountId.ToString()))
            .SendAsync("status", new { accountId, status });
}
