#pragma warning disable CA1707 // Scenario-based test names use underscores.
using System.Text.Json;
using RiskAlert.Core;

namespace RiskAlert.Tests;

public sealed class RuleTests
{
    [Theory]
    [InlineData(10000, null)]
    [InlineData(10001, Severity.Medium)]
    [InlineData(20000, Severity.Medium)]
    [InlineData(20001, Severity.High)]
    [InlineData(50000, Severity.High)]
    [InlineData(50001, Severity.Critical)]
    public async Task EvaluateAsync_LargeThresholds_UsesStrictBoundaries(int amount, Severity? severity)
    {
        var clock = new ManualTimeProvider(); var rule = new LargeTransactionRule(); var current = Samples.Event(clock, amount);
        var hits = await rule.EvaluateAsync(Samples.Context(rule, current, clock, current), CancellationToken.None);
        if (severity is null) Assert.Empty(hits); else Assert.Equal(severity, Assert.Single(hits).Severity);
    }
    [Theory]
    [InlineData("KRW", TransactionType.Deposit, 10000001, Severity.Medium)]
    [InlineData("USD", TransactionType.Withdrawal, 20001, Severity.High)]
    [InlineData("USDT", TransactionType.Deposit, 50001, Severity.Critical)]
    public async Task EvaluateAsync_LargeCurrencyAndType_UsesOwnThreshold(string currency, TransactionType type, int amount, Severity expected)
    {
        var clock = new ManualTimeProvider(); var rule = new LargeTransactionRule(); var current = Samples.Event(clock, amount, type, currency: currency);
        Assert.Equal(expected, Assert.Single(await rule.EvaluateAsync(Samples.Context(rule, current, clock), CancellationToken.None)).Severity);
        Assert.Empty(await rule.EvaluateAsync(Samples.Context(rule, current with { Type = TransactionType.BonusGrant }, clock), CancellationToken.None));
    }
    [Theory]
    [InlineData(4, null)] [InlineData(5, Severity.Medium)] [InlineData(10, Severity.High)]
    public async Task EvaluateAsync_VelocityCounts_TriggersAndEscalates(int count, Severity? expected)
    {
        var clock = new ManualTimeProvider(); var rule = new VelocityRule();
        var events = Enumerable.Range(0, count).Select(_ => Samples.Event(clock)).ToArray();
        var hits = await rule.EvaluateAsync(Samples.Context(rule, events[^1], clock, events), CancellationToken.None);
        if (expected is null) Assert.Empty(hits); else Assert.Equal(expected, Assert.Single(hits).Severity);
    }
    [Fact]
    public async Task EvaluateAsync_VelocityOtherCurrencyOrOldEvent_ExcludesUnrelatedEvents()
    {
        var clock = new ManualTimeProvider(); var rule = new VelocityRule(); var current = Samples.Event(clock);
        var events = new[] { current, Samples.Event(clock, currency: "KRW"), Samples.Event(clock, member: "other"),
            Samples.Event(clock) with { OccurredAt = clock.GetUtcNow().AddMinutes(-11) }, Samples.Event(clock, type: TransactionType.Withdrawal) };
        Assert.Empty(await rule.EvaluateAsync(Samples.Context(rule, current, clock, events), CancellationToken.None));
    }
    [Theory]
    [InlineData(1, null)] [InlineData(2, Severity.High)] [InlineData(3, Severity.Critical)]
    public async Task EvaluateAsync_SharedExact_UsesDistinctMembersAndMasksAccount(int count, Severity? expected)
    {
        var clock = new ManualTimeProvider(); var rule = new SharedPayoutAccountRule();
        var events = Enumerable.Range(0, count).Select(i => Samples.Event(clock, type: TransactionType.Withdrawal, member: "member-" + i, account: "123456789012", holder: "Sample Name")).ToArray();
        var hits = await rule.EvaluateAsync(Samples.Context(rule, events[^1], clock, events.Concat(events).ToArray()), CancellationToken.None);
        if (expected is null) { Assert.Empty(hits); return; }
        var hit = Assert.Single(hits); Assert.Equal(expected, hit.Severity);
        var details = JsonSerializer.Serialize(hit.Details);
        Assert.Contains("****789012", details); Assert.DoesNotContain("123456789012", details);
        Assert.DoesNotContain("123456789012", hit.FingerprintKey);
    }
    [Theory]
    [InlineData("Sample Receiver", true)] [InlineData("Sample Receive", true)] [InlineData("Another Person", false)] [InlineData("", false)]
    public async Task EvaluateAsync_LastDigits_RequiresSimilarHolder(string holder, bool expected)
    {
        var clock = new ManualTimeProvider(); var rule = new SharedPayoutAccountRule();
        var a = Samples.Event(clock, type: TransactionType.Withdrawal, account: "1111567890", holder: "Sample Receiver");
        var b = Samples.Event(clock, type: TransactionType.Withdrawal, member: "member-2", account: "2222567890", holder: holder);
        var context = Samples.Context(rule, b, clock, a, b) with { ParametersJson = "{\"matchMode\":\"LastDigits\",\"digits\":6}" };
        var hits = await rule.EvaluateAsync(context, CancellationToken.None);
        Assert.Equal(expected ? 1 : 0, hits.Count);
        Assert.Empty(await rule.EvaluateAsync(context with { ParametersJson = "{}" }, CancellationToken.None));
    }
    [Fact]
    public async Task EvaluateAsync_SharedOutsideLookbackOrDifferentBank_DoesNotMatch()
    {
        var clock = new ManualTimeProvider(); var rule = new SharedPayoutAccountRule();
        var current = Samples.Event(clock, type: TransactionType.Withdrawal, account: "123456789012");
        var other = current with { EventId = "other", MemberId = "other", OccurredAt = current.OccurredAt.AddDays(-31) };
        Assert.Empty(await rule.EvaluateAsync(Samples.Context(rule, current, clock, current, other), CancellationToken.None));
        var different = PayoutPrivacy.Protect(new TransactionEvent { EventId = "different", MemberId = "other", Type = TransactionType.Withdrawal,
            PayoutBankCode = "OtherDemo", PayoutAccountNo = "123456789012", OccurredAt = current.OccurredAt });
        Assert.Empty(await rule.EvaluateAsync(Samples.Context(rule, current, clock, current, different), CancellationToken.None));
    }
    [Theory]
    [InlineData(100, 50, 0, null)] [InlineData(99, 200, 0, null)]
    [InlineData(100, 30, 21, Severity.Medium)] [InlineData(100, 50, 51, Severity.High)]
    public async Task EvaluateAsync_BonusRatio_CombinesBonusTypesAndChecksMinimum(int deposit, int grant, int conversion, Severity? expected)
    {
        var clock = new ManualTimeProvider(); var rule = new BonusRatioRule();
        var events = new[] { Samples.Event(clock, deposit), Samples.Event(clock, grant, TransactionType.BonusGrant), Samples.Event(clock, conversion, TransactionType.BonusConversion) };
        var hits = await rule.EvaluateAsync(Samples.Context(rule, events[^1], clock, events), CancellationToken.None);
        if (expected is null) Assert.Empty(hits); else Assert.Equal(expected, Assert.Single(hits).Severity);
    }
    [Fact]
    public async Task EvaluateAsync_BonusOtherCurrencyAndOldDeposit_DoesNotMixCurrenciesOrDivideByZero()
    {
        var clock = new ManualTimeProvider(); var rule = new BonusRatioRule(); var bonus = Samples.Event(clock, 1000, TransactionType.BonusGrant);
        Assert.Empty(await rule.EvaluateAsync(Samples.Context(rule, bonus, clock, bonus, Samples.Event(clock, 10000, currency: "KRW"),
            Samples.Event(clock, 200) with { OccurredAt = clock.GetUtcNow().AddDays(-8) }), CancellationToken.None));
    }
}
