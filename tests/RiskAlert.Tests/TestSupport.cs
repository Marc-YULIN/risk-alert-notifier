using RiskAlert.Core;

namespace RiskAlert.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 12, 4, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}
internal sealed class MemoryHistory(IReadOnlyList<TransactionEvent> events) : ITransactionHistory
{
    public Task<IReadOnlyList<TransactionEvent>> ForMemberAsync(string memberId, TransactionType? type, string currency,
        DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TransactionEvent>>(
        events.Where(x => x.MemberId == memberId && x.Currency == currency && (type == null || x.Type == type) && x.OccurredAt >= from && x.OccurredAt <= until).ToArray());
    public Task<IReadOnlyList<TransactionEvent>> ForPayoutAsync(TransactionEvent current, string matchMode, int digits,
        DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TransactionEvent>>(
        events.Where(x => x.Type == TransactionType.Withdrawal && x.OccurredAt >= from && x.OccurredAt <= until &&
            (matchMode == "Exact" ? x.PayoutAccountHash == current.PayoutAccountHash : x.PayoutAccountSuffix?.EndsWith(current.PayoutAccountSuffix![^digits..], StringComparison.Ordinal) == true)).ToArray());
}
internal static class Samples
{
    public static TransactionEvent Event(ManualTimeProvider clock, decimal amount = 100, TransactionType type = TransactionType.Deposit,
        string member = "member-1", string currency = "USD", string? account = null, string? holder = null) => PayoutPrivacy.Protect(new()
        { EventId = Guid.NewGuid().ToString(), MemberId = member, Currency = currency, Type = type, Amount = amount,
            OccurredAt = clock.GetUtcNow(), PayoutBankCode = "DemoBank", PayoutAccountNo = account, PayoutAccountHolder = holder });
    public static RuleContext Context(IRiskRule rule, TransactionEvent current, ManualTimeProvider clock, params TransactionEvent[] events) =>
        new(current, clock, new MemoryHistory(events), RuleParameters.Defaults(rule.Code));
    public static RuleHit Hit(ManualTimeProvider clock, Severity severity = Severity.Medium) => new("LARGE_TXN", severity,
        "member-1", "USD", 20000, "USD:Deposit", clock.GetUtcNow().AddMinutes(-10), clock.GetUtcNow(),
        new Dictionary<string, object> { ["threshold"] = 10000m }, [Guid.NewGuid().ToString()]);
}
