using Microsoft.AspNetCore.SignalR;
using RiskAlert.Core;

namespace RiskAlert.Api.Hubs;

public sealed class AlertsHub : Hub
{
    public override Task OnConnectedAsync() => Groups.AddToGroupAsync(Context.ConnectionId, "all", Context.ConnectionAborted);
    public async Task JoinCurrency(string currency)
    {
        if (!RuleParameters.CurrencyValid(currency)) throw new HubException("Invalid currency.");
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "all", Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, "currency:" + currency, Context.ConnectionAborted);
    }
    public async Task LeaveCurrency(string currency)
    {
        if (!RuleParameters.CurrencyValid(currency)) throw new HubException("Invalid currency.");
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "currency:" + currency, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, "all", Context.ConnectionAborted);
    }
}
public sealed class SignalRAlertBroadcaster(IHubContext<AlertsHub> hub) : IAlertBroadcaster
{
    public Task BroadcastAsync(Alert alert, bool isNew, CancellationToken cancellationToken) =>
        hub.Clients.Groups("all", "currency:" + alert.Currency).SendAsync(isNew ? "alertRaised" : "alertUpdated", alert, cancellationToken);
}
