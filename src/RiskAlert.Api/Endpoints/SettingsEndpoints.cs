using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RiskAlert.Core;
using RiskAlert.Infrastructure;

namespace RiskAlert.Api.Endpoints;

public sealed record RuleUpdate(bool Enabled, JsonElement Parameters);
public sealed record NotificationUpdate(bool Enabled, Severity MinSeverity, string? ChatIdOverride);
public static class RuleEndpoints
{
    public static RouteGroupBuilder MapRules(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rules").WithTags("Rules");
        group.MapGet("", async (RuleSettingsProvider provider, CancellationToken cancellationToken) => Results.Ok(await provider.GetAsync(cancellationToken)))
            .WithSummary("List effective rule settings");
        group.MapPut("/{code}", async (string code, RuleUpdate input, RuleSettingsProvider provider, IngestGate gate, CancellationToken cancellationToken) =>
        {
            if (input.Parameters.ValueKind != JsonValueKind.Object) return ApiProblems.Error(400, "InvalidParameters", "Parameters must be an object.");
            await gate.Semaphore.WaitAsync(cancellationToken);
            try
            {
                return await provider.UpdateAsync(code, input.Enabled, input.Parameters.GetRawText(), cancellationToken)
                    ? Results.NoContent() : ApiProblems.Error(400, "InvalidParameters", "Unknown rule or invalid parameters.");
            }
            finally { gate.Semaphore.Release(); }
        }).WithSummary("Replace rule parameters and invalidate the cache");
        return group;
    }
}
public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotifications(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Notifications");
        group.MapGet("/notification-settings", async (RiskAlertDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await db.NotificationSettings.AsNoTracking().OrderBy(x => x.Currency).ToArrayAsync(cancellationToken)))
            .WithSummary("List per-currency notification policy");
        group.MapPut("/notification-settings/{currency}", async (string currency, NotificationUpdate input, RiskAlertDbContext db, IngestGate gate, CancellationToken cancellationToken) =>
        {
            if ((currency != "*" && !RuleParameters.CurrencyValid(currency)) || !Enum.IsDefined(input.MinSeverity) || (input.ChatIdOverride?.Length ?? 0) > 100)
                return ApiProblems.Error(400, "InvalidSetting", "Invalid notification setting.");
            await gate.Semaphore.WaitAsync(cancellationToken);
            try
            {
                var setting = await db.NotificationSettings.FindAsync([currency], cancellationToken);
                if (setting is null) { setting = new NotificationSetting { Currency = currency }; db.Add(setting); }
                setting.Enabled = input.Enabled; setting.MinSeverity = input.MinSeverity; setting.ChatIdOverride = input.ChatIdOverride;
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(setting);
            }
            finally { gate.Semaphore.Release(); }
        }).WithSummary("Set a currency notification switch and severity floor");
        group.MapGet("/notifications", async (Guid? alertId, RiskAlertDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await db.Notifications.AsNoTracking().Where(x => alertId == null || x.AlertId == alertId)
                .OrderByDescending(x => x.SentAt).Take(500).ToArrayAsync(cancellationToken)))
            .WithSummary("Inspect outbound delivery and skip records");
        return group;
    }
}
public static class StatsEndpoints
{
    public static RouteGroupBuilder MapStats(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stats").WithTags("Statistics");
        group.MapGet("/summary", async (int? hours, RiskAlertDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var window = hours ?? 24;
            if (window is < 1 or > 8760) return ApiProblems.Error(400, "InvalidHours", "Hours must be between 1 and 8760.");
            var from = clock.GetUtcNow().AddHours(-window);
            var items = await db.Alerts.AsNoTracking().Where(x => x.LastSeenAt >= from).ToArrayAsync(cancellationToken);
            return Results.Ok(new { total = items.Length, open = items.Count(x => x.Status != AlertStatus.Resolved),
                critical = items.Count(x => x.Severity == Severity.Critical),
                byRule = items.GroupBy(x => x.RuleCode).ToDictionary(x => x.Key, x => x.Count()),
                bySeverity = items.GroupBy(x => x.Severity).ToDictionary(x => x.Key.ToString(), x => x.Count()),
                byCurrency = items.GroupBy(x => x.Currency).ToDictionary(x => x.Key, x => x.Count()) });
        }).WithSummary("Count distinct alert rows active in the requested lookback");
        return group;
    }
}
public static class LocalizationEndpoints
{
    public static RouteGroupBuilder MapLocalization(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/i18n").WithTags("Localization");
        group.MapGet("/{locale}", (string locale) => Results.Ok(AlertTextCatalog.Get(locale))).WithSummary("Get the shared dashboard and notification catalog");
        return group;
    }
}
