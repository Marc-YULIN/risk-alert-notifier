#pragma warning disable CA1707 // Scenario-based test names use underscores.
using RiskAlert.Core;

namespace RiskAlert.Tests;

public sealed class PolicyAndPresentationTests
{
    [Fact]
    public void MergeOrCreate_WithinWindow_MergesEvidenceAndEscalates()
    {
        var clock = new ManualTimeProvider(); var hit = Samples.Hit(clock);
        var original = AlertDeduplicator.MergeOrCreate(hit, [], clock.GetUtcNow(), 30);
        original.Status = AlertStatus.Acknowledged;
        clock.Advance(TimeSpan.FromMinutes(5));
        var next = Samples.Hit(clock, Severity.Critical) with { WindowStart = hit.WindowStart.AddMinutes(-2) };
        var result = AlertDeduplicator.MergeOrCreate(next, [original], clock.GetUtcNow(), 30);
        Assert.Same(original, result); Assert.Equal(2, result.OccurrenceCount); Assert.Equal(Severity.Critical, result.Severity);
        Assert.Equal(next.WindowStart, result.WindowStart); Assert.Equal(next.WindowEnd, result.WindowEnd);
        Assert.Contains(hit.SourceEventIds[0], result.SourceEventIdsJson); Assert.Contains(next.SourceEventIds[0], result.SourceEventIdsJson);
        Assert.Equal(AlertStatus.Acknowledged, result.Status);
        AlertDeduplicator.MergeOrCreate(hit, [original], clock.GetUtcNow(), 30); Assert.Equal(Severity.Critical, original.Severity);
    }
    [Theory]
    [InlineData(31, AlertStatus.Open)] [InlineData(0, AlertStatus.Resolved)]
    public void MergeOrCreate_ExpiredOrResolved_CreatesNew(int minutes, AlertStatus status)
    {
        var clock = new ManualTimeProvider(); var hit = Samples.Hit(clock);
        var original = AlertDeduplicator.MergeOrCreate(hit, [], clock.GetUtcNow(), 30); original.Status = status;
        clock.Advance(TimeSpan.FromMinutes(minutes));
        Assert.NotEqual(original.Id, AlertDeduplicator.MergeOrCreate(hit, [original], clock.GetUtcNow(), 30).Id);
    }
    [Fact]
    public void ShouldSend_CurrencyPolicy_UsesFallbackAndEscalationOnly()
    {
        var alert = new Alert { Currency = "USD", Severity = Severity.High };
        var fallback = new NotificationSetting();
        Assert.True(NotificationPolicy.ShouldSend(alert, [fallback]).Send);
        Assert.Equal("CurrencyDisabled", NotificationPolicy.ShouldSend(alert, [fallback, new() { Currency = "USD", Enabled = false }]).Reason);
        Assert.Equal("BelowMinimumSeverity", NotificationPolicy.ShouldSend(alert, [new() { MinSeverity = Severity.Critical }]).Reason);
        Assert.Equal("MergedWithoutEscalation", NotificationPolicy.ShouldSend(alert, [fallback], false, Severity.High).Reason);
        Assert.True(NotificationPolicy.ShouldSend(alert, [fallback], false, Severity.Medium).Send);
        Assert.False(NotificationPolicy.ShouldSend(alert, []).Send);
    }
    [Theory]
    [InlineData("KRW", "KRW 12,500,000", 12500000)] [InlineData("USD", "USD 10,250.00", 10250)]
    [InlineData("USDT", "USDT 1.00", 1)] [InlineData("JPY", "JPY 1", 1)] [InlineData("EUR", "EUR 1.00", 1)]
    public void Format_CurrencyPrecision_UsesInvariantGrouping(string currency, string expected, decimal amount) =>
        Assert.Equal(expected, MoneyFormatter.Format(amount, currency));
    [Fact]
    public void Escape_AllReservedCharacters_EscapesEveryCharacterOnce()
    {
        const string reserved = "_*[]()~`>#+-=|{}.!\\";
        Assert.Equal(string.Concat(reserved.Select(c => "\\" + c)), TelegramMarkdown.Escape(reserved));
        Assert.Equal("Hello 123 中文", TelegramMarkdown.Escape("Hello 123 中文"));
    }
    [Theory]
    [InlineData("en")] [InlineData("zh-TW")] [InlineData("ko")]
    public void Render_SupportedLocales_ContainsAllKeys(string locale)
    {
        var catalog = AlertTextCatalog.Get(locale);
        Assert.Equal(AlertTextCatalog.Get("en").Keys.Order(), catalog.Keys.Order());
        foreach (var code in RuleParameters.Codes)
        foreach (var severity in Enum.GetValues<Severity>())
        {
            var text = AlertTextCatalog.Render(new Alert { RuleCode = code, Severity = severity, MemberId = "sample", Amount = 10, Currency = "USD" }, locale);
            Assert.Contains(catalog[code], text.Title); Assert.Contains(catalog[severity.ToString()], text.Title);
            Assert.Contains("sample", text.Body); Assert.Contains("USD 10.00", text.Body);
        }
        Assert.Same(AlertTextCatalog.Get("en"), AlertTextCatalog.Get("unknown"));
    }
}
