using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RiskAlert.Core;

public sealed record LargeParameters
{
    public Dictionary<string, Dictionary<TransactionType, decimal>> Thresholds { get; init; } = new()
    {
        ["KRW"] = new() { [TransactionType.Deposit] = 10_000_000, [TransactionType.Withdrawal] = 10_000_000 },
        ["USD"] = new() { [TransactionType.Deposit] = 10_000, [TransactionType.Withdrawal] = 10_000 },
        ["USDT"] = new() { [TransactionType.Deposit] = 10_000, [TransactionType.Withdrawal] = 10_000 }
    };
}
public sealed record VelocityParameters
{
    public int WindowMinutes { get; init; } = 10;
    public Dictionary<TransactionType, int> MaxCount { get; init; } = new()
        { [TransactionType.Deposit] = 5, [TransactionType.Withdrawal] = 3 };
}
public sealed record SharedParameters
{
    public int LookbackDays { get; init; } = 30;
    public string MatchMode { get; init; } = "Exact";
    public int Digits { get; init; } = 6;
}
public sealed record BonusParameters
{
    public int LookbackDays { get; init; } = 7;
    public decimal MaxRatio { get; init; } = 0.5m;
    public Dictionary<string, decimal> MinDeposit { get; init; } = new()
        { ["KRW"] = 100_000, ["USD"] = 100, ["USDT"] = 100 };
}
public static class RuleParameters
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static readonly string[] Codes = ["LARGE_TXN", "VELOCITY", "SHARED_PAYOUT_ACCOUNT", "BONUS_RATIO"];
    public static T Read<T>(string json) where T : new() => JsonSerializer.Deserialize<T>(json, Json) ?? new T();
    public static string Defaults(string code) => code switch
    {
        "LARGE_TXN" => JsonSerializer.Serialize(new LargeParameters(), Json),
        "VELOCITY" => JsonSerializer.Serialize(new VelocityParameters(), Json),
        "SHARED_PAYOUT_ACCOUNT" => JsonSerializer.Serialize(new SharedParameters(), Json),
        "BONUS_RATIO" => JsonSerializer.Serialize(new BonusParameters(), Json),
        _ => throw new ArgumentException("Unknown rule.")
    };
    public static bool Valid(string code, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return code switch
            {
                "LARGE_TXN" => Read<LargeParameters>(json).Thresholds is { Count: > 0 } t &&
                    t.All(x => CurrencyValid(x.Key) && x.Value is { Count: > 0 } && x.Value.All(v =>
                        v.Key is TransactionType.Deposit or TransactionType.Withdrawal && v.Value > 0 && v.Value <= 100_000_000_000_000m)),
                "VELOCITY" => Read<VelocityParameters>(json) is var v && v.WindowMinutes is > 0 and <= 10080 &&
                    v.MaxCount is { Count: > 0 } && v.MaxCount.All(x => x.Key is TransactionType.Deposit or TransactionType.Withdrawal && x.Value is > 0 and <= 100000),
                "SHARED_PAYOUT_ACCOUNT" => Read<SharedParameters>(json) is var s && s.LookbackDays is > 0 and <= 365 && s.Digits is >= 1 and <= 6 && s.MatchMode is "Exact" or "LastDigits",
                "BONUS_RATIO" => Read<BonusParameters>(json) is var b && b.LookbackDays is > 0 and <= 365 && b.MaxRatio > 0 &&
                    b.MinDeposit is { Count: > 0 } && b.MinDeposit.All(x => CurrencyValid(x.Key) && x.Value > 0),
                _ => false
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { return false; }
    }
    public static bool CurrencyValid(string value) => value.Length is >= 3 and <= 4 && value.All(c => c is >= 'A' and <= 'Z');
}

public static class PayoutPrivacy
{
    public static string Digits(string? value) => new((value ?? "").Where(char.IsAsciiDigit).ToArray());
    public static string Hash(string bank, string account) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bank + ":" + account)));
    public static string Mask(string? account) => "****" + Digits(account)[^Math.Min(6, Digits(account).Length)..];
    public static string NormalizeName(string? value) => new((value ?? "").Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    public static double Similarity(string? left, string? right)
    {
        var a = NormalizeName(left); var b = NormalizeName(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = old;
            }
        }
        return 1d - (double)row[b.Length] / Math.Max(a.Length, b.Length);
    }
    public static TransactionEvent Protect(TransactionEvent value)
    {
        var digits = Digits(value.PayoutAccountNo);
        return value with { PayoutAccountNo = digits.Length == 0 ? null : Mask(digits),
            PayoutAccountHash = digits.Length == 0 ? null : Hash(value.PayoutBankCode ?? "", digits),
            PayoutAccountSuffix = digits.Length == 0 ? null : digits[^Math.Min(6, digits.Length)..] };
    }
}

public sealed class LargeTransactionRule : IRiskRule
{
    public string Code => "LARGE_TXN";
    public Task<IReadOnlyList<RuleHit>> EvaluateAsync(RuleContext ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var e = ctx.Event; var p = RuleParameters.Read<LargeParameters>(ctx.ParametersJson);
        if (!p.Thresholds.TryGetValue(e.Currency, out var types) || !types.TryGetValue(e.Type, out var threshold) || e.Amount <= threshold)
            return Task.FromResult<IReadOnlyList<RuleHit>>([]);
        var severity = e.Amount > threshold * 5 ? Severity.Critical : e.Amount > threshold * 2 ? Severity.High : Severity.Medium;
        return Task.FromResult<IReadOnlyList<RuleHit>>([new(Code, severity, e.MemberId, e.Currency, e.Amount,
            $"{e.Currency}:{e.Type}", e.OccurredAt, e.OccurredAt,
            new Dictionary<string, object> { ["threshold"] = threshold, ["type"] = e.Type.ToString() }, [e.EventId])]);
    }
}
public sealed class VelocityRule : IRiskRule
{
    public string Code => "VELOCITY";
    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(RuleContext ctx, CancellationToken cancellationToken)
    {
        var e = ctx.Event; var p = RuleParameters.Read<VelocityParameters>(ctx.ParametersJson);
        if (!p.MaxCount.TryGetValue(e.Type, out var limit)) return [];
        var from = e.OccurredAt.AddMinutes(-p.WindowMinutes);
        var events = await ctx.History.ForMemberAsync(e.MemberId, e.Type, e.Currency, from, e.OccurredAt, cancellationToken);
        if (events.Count < limit) return [];
        return [new(Code, events.Count >= limit * 2 ? Severity.High : Severity.Medium, e.MemberId, e.Currency, e.Amount,
            $"{e.Currency}:{e.Type}", from, e.OccurredAt,
            new Dictionary<string, object> { ["count"] = events.Count, ["windowMinutes"] = p.WindowMinutes, ["type"] = e.Type.ToString() }, events.Select(x => x.EventId).ToArray())];
    }
}
public sealed class SharedPayoutAccountRule : IRiskRule
{
    public string Code => "SHARED_PAYOUT_ACCOUNT";
    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(RuleContext ctx, CancellationToken cancellationToken)
    {
        var e = ctx.Event; var p = RuleParameters.Read<SharedParameters>(ctx.ParametersJson);
        if (e.Type != TransactionType.Withdrawal || string.IsNullOrEmpty(e.PayoutAccountHash)) return [];
        if (p.MatchMode == "LastDigits" && (e.PayoutAccountSuffix?.Length ?? 0) < p.Digits) return [];
        var from = e.OccurredAt.AddDays(-p.LookbackDays);
        var events = await ctx.History.ForPayoutAsync(e, p.MatchMode, p.Digits, from, e.OccurredAt, cancellationToken);
        var matching = events.Where(x => p.MatchMode == "Exact" || PayoutPrivacy.Similarity(x.PayoutAccountHolder, e.PayoutAccountHolder) >= 0.85).ToArray();
        var count = matching.Select(x => x.MemberId).Distinct().Count();
        if (count < 2) return [];
        var key = p.MatchMode == "Exact" ? e.PayoutAccountHash : PayoutPrivacy.Hash("suffix", e.PayoutAccountSuffix![^p.Digits..]);
        return [new(Code, count >= 3 ? Severity.Critical : Severity.High, e.MemberId, e.Currency, e.Amount,
            $"{e.Currency}:{p.MatchMode}:{key}", from, e.OccurredAt,
            new Dictionary<string, object> { ["account"] = e.PayoutAccountNo ?? "****", ["memberCount"] = count, ["matchMode"] = p.MatchMode }, matching.Select(x => x.EventId).ToArray())];
    }
}
public sealed class BonusRatioRule : IRiskRule
{
    public string Code => "BONUS_RATIO";
    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(RuleContext ctx, CancellationToken cancellationToken)
    {
        var e = ctx.Event; var p = RuleParameters.Read<BonusParameters>(ctx.ParametersJson);
        if (!p.MinDeposit.TryGetValue(e.Currency, out var minimum)) return [];
        var from = e.OccurredAt.AddDays(-p.LookbackDays);
        var events = await ctx.History.ForMemberAsync(e.MemberId, null, e.Currency, from, e.OccurredAt, cancellationToken);
        var deposits = events.Where(x => x.Type == TransactionType.Deposit).Sum(x => x.Amount);
        var bonus = events.Where(x => x.Type is TransactionType.BonusGrant or TransactionType.BonusConversion).Sum(x => x.Amount);
        if (deposits < minimum || bonus / deposits <= p.MaxRatio) return [];
        return [new(Code, bonus / deposits > 1 ? Severity.High : Severity.Medium, e.MemberId, e.Currency, bonus,
            e.Currency, from, e.OccurredAt, new Dictionary<string, object> { ["deposits"] = deposits, ["bonus"] = bonus, ["ratio"] = bonus / deposits },
            events.Where(x => x.Type != TransactionType.Withdrawal).Select(x => x.EventId).ToArray())];
    }
}
