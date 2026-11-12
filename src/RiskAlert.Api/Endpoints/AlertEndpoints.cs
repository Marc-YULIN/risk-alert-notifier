using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RiskAlert.Core;
using RiskAlert.Infrastructure;

namespace RiskAlert.Api.Endpoints;

public sealed record OperatorRequest(string By);
public static class AlertEndpoints
{
    public static RouteGroupBuilder MapAlerts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alerts").WithTags("Alerts");
        group.MapGet("", async (RiskAlertDbContext db, string? status, string? currency, string? severity,
            string? memberId, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, CancellationToken cancellationToken) =>
        {
            var number = page ?? 1; var size = pageSize ?? 50;
            if (number is < 1 or > 100000 || size is < 1 or > 200 || from > to ||
                (status is not null && (!Enum.TryParse<AlertStatus>(status, true, out var s) || !Enum.IsDefined(s))) ||
                (severity is not null && (!Enum.TryParse<Severity>(severity, true, out var v) || !Enum.IsDefined(v))))
                return ApiProblems.Error(400, "InvalidFilter", "Invalid alert filter or pagination.");
            var query = db.Alerts.AsNoTracking();
            if (status is not null) { var parsed = Enum.Parse<AlertStatus>(status, true); query = query.Where(x => x.Status == parsed); }
            if (severity is not null) { var parsed = Enum.Parse<Severity>(severity, true); query = query.Where(x => x.Severity == parsed); }
            if (currency is not null) query = query.Where(x => x.Currency == currency);
            if (memberId is not null) query = query.Where(x => x.MemberId == memberId);
            if (from is not null) query = query.Where(x => x.WindowEnd >= from);
            if (to is not null) query = query.Where(x => x.WindowStart <= to);
            var total = await query.CountAsync(cancellationToken);
            var items = await query.OrderByDescending(x => x.LastSeenAt).ThenBy(x => x.Id).Skip((number - 1) * size).Take(size).ToArrayAsync(cancellationToken);
            return Results.Ok(new { items, total, page = number, pageSize = size });
        }).WithSummary("Search alerts with pagination and overlapping evidence windows");
        group.MapGet("/{id:guid}", async (Guid id, RiskAlertDbContext db, CancellationToken cancellationToken) =>
        {
            var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (alert is null) return ApiProblems.Error(404, "AlertNotFound", "Alert not found.");
            var ids = JsonSerializer.Deserialize<string[]>(alert.SourceEventIdsJson) ?? [];
            var events = await db.Transactions.AsNoTracking().Where(x => ids.Contains(x.EventId)).OrderBy(x => x.OccurredAt)
                .Select(x => new { x.EventId, x.MemberId, x.Type, x.Amount, x.Currency, x.OccurredAt, x.ReceivedAt,
                    x.PayoutBankCode, x.PayoutAccountNo }).ToArrayAsync(cancellationToken);
            return Results.Ok(new { alert, sourceEvents = events });
        }).WithSummary("Get an alert and masked source events");
        group.MapPost("/{id:guid}/acknowledge", (Guid id, OperatorRequest request, RiskAlertDbContext db, IngestGate gate,
            TimeProvider clock, IAlertBroadcaster broadcaster, CancellationToken cancellationToken) =>
            ChangeStatusAsync(id, request, AlertStatus.Acknowledged, db, gate, clock, broadcaster, cancellationToken))
            .WithSummary("Acknowledge an open alert");
        group.MapPost("/{id:guid}/resolve", (Guid id, OperatorRequest request, RiskAlertDbContext db, IngestGate gate,
            TimeProvider clock, IAlertBroadcaster broadcaster, CancellationToken cancellationToken) =>
            ChangeStatusAsync(id, request, AlertStatus.Resolved, db, gate, clock, broadcaster, cancellationToken))
            .WithSummary("Resolve an open or acknowledged alert");
        return group;
    }
    private static async Task<IResult> ChangeStatusAsync(Guid id, OperatorRequest request, AlertStatus target,
        RiskAlertDbContext db, IngestGate gate, TimeProvider clock, IAlertBroadcaster broadcaster, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.By) || request.By.Length > 100) return ApiProblems.Error(400, "InvalidOperator", "Supply an operator name up to 100 characters.");
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var alert = await db.Alerts.FindAsync([id], cancellationToken);
            if (alert is null) return ApiProblems.Error(404, "AlertNotFound", "Alert not found.");
            if (alert.Status == target) return Results.Ok(alert);
            if (alert.Status == AlertStatus.Resolved) return ApiProblems.Error(409, "AlertResolved", "Resolved alerts cannot be reopened.");
            alert.Status = target;
            if (target == AlertStatus.Acknowledged) { alert.AcknowledgedBy = request.By; alert.AcknowledgedAt = clock.GetUtcNow(); }
            await db.SaveChangesAsync(cancellationToken);
            await broadcaster.BroadcastAsync(alert, false, cancellationToken);
            return Results.Ok(alert);
        }
        finally { gate.Semaphore.Release(); }
    }
}
