using System.Text.Json;

namespace RiskAlert.Core;

/// <summary>The supported synthetic payment event categories.</summary>
public enum TransactionType { Deposit, Withdrawal, BonusGrant, BonusConversion }
/// <summary>Ordered alert impact levels.</summary>
public enum Severity { Low, Medium, High, Critical }
/// <summary>The operator-managed alert lifecycle.</summary>
public enum AlertStatus { Open, Acknowledged, Resolved }
/// <summary>The selected notification transport.</summary>
public enum NotificationChannel { Telegram, DryRun }
/// <summary>The persisted delivery outcome.</summary>
public enum DeliveryStatus { Pending, Sent, Failed, Skipped }

public sealed record TransactionEvent
{
    public string EventId { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string? MemberName { get; set; }
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? PayoutBankCode { get; set; }
    public string? PayoutAccountNo { get; set; }
    public string? PayoutAccountHolder { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? PayoutAccountHash { get; set; }
    public string? PayoutAccountSuffix { get; set; }
}

public sealed class Alert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RuleCode { get; set; } = "";
    public Severity Severity { get; set; }
    public string MemberId { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal? Amount { get; set; }
    public string Fingerprint { get; set; } = "";
    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public string SourceEventIdsJson { get; set; } = "[]";
    public AlertStatus Status { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}

public sealed class RuleSetting
{
    public string RuleCode { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ParametersJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class NotificationSetting
{
    public string Currency { get; set; } = "*";
    public bool Enabled { get; set; } = true;
    public Severity MinSeverity { get; set; } = Severity.Medium;
    public string? ChatIdOverride { get; set; }
}

public sealed class OutboundNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AlertId { get; set; }
    public NotificationChannel Channel { get; set; }
    public string Text { get; set; } = "";
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? SkipReason { get; set; }
}

public sealed record RuleHit(string RuleCode, Severity Severity, string MemberId, string Currency,
    decimal? Amount, string FingerprintKey, DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
    IReadOnlyDictionary<string, object> Details, IReadOnlyList<string> SourceEventIds);
public sealed record RuleContext(TransactionEvent Event, TimeProvider TimeProvider,
    ITransactionHistory History, string ParametersJson);
public interface IRiskRule
{
    string Code { get; }
    Task<IReadOnlyList<RuleHit>> EvaluateAsync(RuleContext ctx, CancellationToken cancellationToken);
}
public interface ITransactionHistory
{
    Task<IReadOnlyList<TransactionEvent>> ForMemberAsync(string memberId, TransactionType? type,
        string currency, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransactionEvent>> ForPayoutAsync(TransactionEvent current, string matchMode,
        int digits, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);
}
public interface IAlertBroadcaster
{
    Task BroadcastAsync(Alert alert, bool isNew, CancellationToken cancellationToken);
}
public sealed record ProcessedAlert(Guid Id, string RuleCode, Severity Severity, bool IsNew);
public sealed record ProcessResult(string EventId, bool Duplicate, IReadOnlyList<ProcessedAlert> Alerts);

public sealed class AlertDeduplicator
{
    public static Alert MergeOrCreate(RuleHit hit, IEnumerable<Alert> candidates, DateTimeOffset now, int minutes)
    {
        var fingerprint = JsonSerializer.Serialize(new[] { hit.RuleCode, hit.MemberId, hit.FingerprintKey });
        var alert = candidates.Where(x => x.Fingerprint == fingerprint && x.Status != AlertStatus.Resolved
            && x.LastSeenAt >= now.AddMinutes(-minutes)).OrderByDescending(x => x.LastSeenAt).FirstOrDefault();
        if (alert is null)
            return new Alert { RuleCode = hit.RuleCode, Severity = hit.Severity, MemberId = hit.MemberId,
                Currency = hit.Currency, Amount = hit.Amount, Fingerprint = fingerprint, FirstSeenAt = now,
                LastSeenAt = now, WindowStart = hit.WindowStart, WindowEnd = hit.WindowEnd,
                DetailsJson = JsonSerializer.Serialize(hit.Details), SourceEventIdsJson = JsonSerializer.Serialize(hit.SourceEventIds) };
        alert.OccurrenceCount++;
        alert.LastSeenAt = now;
        alert.WindowStart = alert.WindowStart < hit.WindowStart ? alert.WindowStart : hit.WindowStart;
        alert.WindowEnd = alert.WindowEnd > hit.WindowEnd ? alert.WindowEnd : hit.WindowEnd;
        alert.Severity = (Severity)Math.Max((int)alert.Severity, (int)hit.Severity);
        alert.Amount = hit.Amount;
        alert.DetailsJson = JsonSerializer.Serialize(hit.Details);
        alert.SourceEventIdsJson = JsonSerializer.Serialize((JsonSerializer.Deserialize<string[]>(alert.SourceEventIdsJson) ?? [])
            .Concat(hit.SourceEventIds).Distinct().ToArray());
        return alert;
    }
}
