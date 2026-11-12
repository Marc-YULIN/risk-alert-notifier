using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RiskAlert.Core;

namespace RiskAlert.Infrastructure;

public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
public sealed class MoneyConverter() : ValueConverter<decimal, long>(
    value => checked((long)(value * 10000m)), value => value / 10000m);

public sealed class RiskAlertDbContext(DbContextOptions<RiskAlertDbContext> options) : DbContext(options)
{
    public DbSet<TransactionEvent> Transactions => Set<TransactionEvent>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<RuleSetting> RuleSettings => Set<RuleSetting>();
    public DbSet<NotificationSetting> NotificationSettings => Set<NotificationSetting>();
    public DbSet<OutboundNotification> Notifications => Set<OutboundNotification>();
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<decimal>().HaveConversion<MoneyConverter>();
    }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var events = modelBuilder.Entity<TransactionEvent>();
        events.HasKey(x => x.EventId);
        events.HasIndex(x => new { x.MemberId, x.Type, x.OccurredAt });
        events.HasIndex(x => new { x.PayoutAccountNo, x.OccurredAt });
        events.HasIndex(x => new { x.PayoutAccountHash, x.OccurredAt });
        events.HasIndex(x => new { x.PayoutAccountSuffix, x.OccurredAt });
        modelBuilder.Entity<Alert>().HasIndex(x => new { x.Fingerprint, x.Status });
        modelBuilder.Entity<RuleSetting>().HasKey(x => x.RuleCode);
        modelBuilder.Entity<NotificationSetting>().HasKey(x => x.Currency);
        modelBuilder.Entity<OutboundNotification>().HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId);
    }
}
public sealed class EfTransactionHistory(RiskAlertDbContext db) : ITransactionHistory
{
    public async Task<IReadOnlyList<TransactionEvent>> ForMemberAsync(string memberId, TransactionType? type,
        string currency, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) =>
        await db.Transactions.AsNoTracking().Where(x => x.MemberId == memberId && x.Currency == currency &&
            (type == null || x.Type == type) && x.OccurredAt >= from && x.OccurredAt <= until).ToArrayAsync(cancellationToken);
    public async Task<IReadOnlyList<TransactionEvent>> ForPayoutAsync(TransactionEvent current, string matchMode,
        int digits, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var query = db.Transactions.AsNoTracking().Where(x => x.Type == TransactionType.Withdrawal &&
            x.OccurredAt >= from && x.OccurredAt <= until);
        if (matchMode == "Exact") query = query.Where(x => x.PayoutAccountHash == current.PayoutAccountHash);
        else
        {
            var suffix = current.PayoutAccountSuffix![^digits..];
            query = query.Where(x => x.PayoutAccountSuffix != null && x.PayoutAccountSuffix.EndsWith(suffix));
        }
        return await query.ToArrayAsync(cancellationToken);
    }
}
