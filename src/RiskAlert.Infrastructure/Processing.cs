using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RiskAlert.Core;

namespace RiskAlert.Infrastructure;

/// <summary>Serializes writes for the single-process SQLite deployment.</summary>
public sealed class IngestGate : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public void Dispose() => Semaphore.Dispose();
}
public sealed class RuleSettingsCache(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private IReadOnlyList<RuleSetting>? settings;
    private DateTimeOffset expires;
    public IReadOnlyList<RuleSetting>? Get()
    {
        lock (sync) return timeProvider.GetUtcNow() < expires ? settings : null;
    }
    public void Set(IReadOnlyList<RuleSetting> values)
    {
        lock (sync) { settings = values; expires = timeProvider.GetUtcNow().AddSeconds(30); }
    }
    public void Invalidate() { lock (sync) settings = null; }
}
public sealed class RuleSettingsProvider(RiskAlertDbContext db, RuleSettingsCache cache,
    IOptions<RiskOptions> options, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<RuleSetting>> GetAsync(CancellationToken cancellationToken)
    {
        if (cache.Get() is { } existing) return existing;
        var values = await db.RuleSettings.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var code in RuleParameters.Codes.Except(values.Select(x => x.RuleCode)))
        {
            var setting = new RuleSetting { RuleCode = code, ParametersJson = options.Value.Rules.GetValueOrDefault(code) ?? RuleParameters.Defaults(code), UpdatedAt = timeProvider.GetUtcNow() };
            db.RuleSettings.Add(setting); values.Add(setting);
        }
        await db.SaveChangesAsync(cancellationToken);
        cache.Set(values);
        return values;
    }
    public async Task<bool> UpdateAsync(string code, bool enabled, string json, CancellationToken cancellationToken)
    {
        if (!RuleParameters.Valid(code, json)) return false;
        var setting = await db.RuleSettings.FindAsync([code], cancellationToken);
        if (setting is null) { setting = new RuleSetting { RuleCode = code }; db.Add(setting); }
        setting.Enabled = enabled; setting.ParametersJson = json; setting.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        cache.Invalidate();
        return true;
    }
}

public sealed class RuleEngine(RiskAlertDbContext db, ITransactionHistory history, IEnumerable<IRiskRule> rules,
    RuleSettingsProvider settings, IOptions<RiskOptions> options, IngestGate gate, TimeProvider timeProvider,
    IAlertBroadcaster broadcaster, NotificationDispatcher dispatcher, ILogger<RuleEngine> logger)
{
    public async Task<ProcessResult> ProcessAsync(TransactionEvent incoming, CancellationToken cancellationToken)
    {
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (await db.Transactions.AnyAsync(x => x.EventId == incoming.EventId, cancellationToken))
                return new(incoming.EventId, true, []);
            var configured = await settings.GetAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var current = PayoutPrivacy.Protect(incoming) with { ReceivedAt = timeProvider.GetUtcNow() };
            db.Transactions.Add(current);
            await db.SaveChangesAsync(cancellationToken);
            var candidates = await db.Alerts.Where(x => x.Status != AlertStatus.Resolved &&
                x.LastSeenAt >= timeProvider.GetUtcNow().AddMinutes(-options.Value.DedupeWindowMinutes)).ToListAsync(cancellationToken);
            var changed = new List<(Alert Alert, bool IsNew)>();
            var notifications = new List<Guid>();
            foreach (var rule in rules)
            {
                var setting = configured.First(x => x.RuleCode == rule.Code);
                if (!setting.Enabled) continue;
                IReadOnlyList<RuleHit> hits;
                try { hits = await rule.EvaluateAsync(new(current, timeProvider, history, setting.ParametersJson), cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    InfrastructureLog.RuleFailed(logger, rule.Code, ex.GetType().Name);
                    continue;
                }
                foreach (var hit in hits)
                {
                    var previous = candidates.ToDictionary(x => x.Id, x => x.Severity);
                    var alert = AlertDeduplicator.MergeOrCreate(hit, candidates, timeProvider.GetUtcNow(), options.Value.DedupeWindowMinutes);
                    var isNew = !previous.ContainsKey(alert.Id);
                    if (isNew) { db.Alerts.Add(alert); candidates.Add(alert); }
                    var outbound = await dispatcher.PrepareAsync(db, alert, isNew, previous.GetValueOrDefault(alert.Id), cancellationToken);
                    if (outbound.Status == DeliveryStatus.Pending) notifications.Add(outbound.Id);
                    changed.Add((alert, isNew));
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            foreach (var id in notifications) dispatcher.Enqueue(id);
            foreach (var (alert, isNew) in changed)
            {
                try { await broadcaster.BroadcastAsync(alert, isNew, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { InfrastructureLog.BroadcastFailed(logger, ex.GetType().Name); }
            }
            return new(incoming.EventId, false, changed.Select(x => new ProcessedAlert(x.Alert.Id, x.Alert.RuleCode, x.Alert.Severity, x.IsNew)).ToArray());
        }
        finally { gate.Semaphore.Release(); }
    }
}
