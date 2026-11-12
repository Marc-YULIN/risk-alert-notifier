using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RiskAlert.Core;

try
{
    var options = SimulatorOptions.Parse(args);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var client = new HttpClient { BaseAddress = new Uri(options.Api), Timeout = TimeSpan.FromSeconds(15) };
    client.DefaultRequestHeaders.Add("X-Api-Key", options.Key);
    var simulator = new Simulator(client, options, TimeProvider.System);
    await simulator.RunAsync(stop.Token);
}
catch (OperationCanceledException) { Console.WriteLine("Simulation cancelled."); }
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine("Invalid arguments. Use --api URL --key KEY --members 50 --duration 60 --rate 5 --scenario all|large|velocity|shared-account|bonus|normal --seed 42.");
    Environment.ExitCode = 1;
}

internal sealed record SimulatorOptions(string Api, string Key, int Members, int Duration, int Rate, string Scenario, int Seed)
{
    public static SimulatorOptions Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Arguments require values.");
        var values = new Dictionary<string, string>();
        var known = new[] { "--api", "--key", "--members", "--duration", "--rate", "--scenario", "--seed" };
        for (var i = 0; i < args.Length; i += 2)
            if (!known.Contains(args[i]) || !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown or repeated argument.");
        int Number(string key, int fallback) => values.TryGetValue(key, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
        var result = new SimulatorOptions(values.GetValueOrDefault("--api", "http://localhost:5107"), values.GetValueOrDefault("--key", "dev-ingest-key"),
            Number("--members", 50), Number("--duration", 60), Number("--rate", 5), values.GetValueOrDefault("--scenario", "all"), Number("--seed", 42));
        if (!Uri.TryCreate(result.Api, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            result.Members is < 1 or > 100000 || result.Duration is < 1 or > 86400 || result.Rate is < 1 or > 500 ||
            result.Scenario is not ("all" or "large" or "velocity" or "shared-account" or "bonus" or "normal")) throw new ArgumentException("Out-of-range argument.");
        return result;
    }
}
internal sealed class Simulator(HttpClient client, SimulatorOptions options, TimeProvider clock)
{
    private readonly Random random = new(options.Seed);
    private readonly string run = Guid.NewGuid().ToString("N")[..10];
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private int sequence;
    private int sent;
    private int alerts;
    private int failures;
    private TransactionEvent Event(string member, TransactionType type, decimal amount, string currency = "USD", string? account = null, string? holder = null) => new()
    {
        EventId = $"sim-{run}-{++sequence}", MemberId = $"{run}-{member}", Type = type, Amount = amount,
        Currency = currency, OccurredAt = clock.GetUtcNow(), PayoutBankCode = account is null ? null : "DemoBank",
        PayoutAccountNo = account, PayoutAccountHolder = holder ?? "Sample Member"
    };
    private IEnumerable<TransactionEvent> Normal()
    {
        for (var i = 0; i < options.Rate; i++)
        {
            var member = random.Next(options.Members);
            var currency = new[] { "KRW", "USD", "USDT" }[random.Next(3)];
            var u1 = 1 - random.NextDouble(); var u2 = 1 - random.NextDouble();
            var normal = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            var amount = (decimal)Math.Clamp(Math.Exp(3 + normal * 0.7), 1, 100);
            amount = currency == "KRW" ? decimal.Round(amount * 1000, 0) : decimal.Round(amount, 2);
            var type = random.NextDouble() < .15 ? TransactionType.Withdrawal : TransactionType.Deposit;
            yield return Event($"normal-{member:000}", type, amount, currency,
                type == TransactionType.Withdrawal ? "7" + Convert.ToInt64(run, 16).ToString("D13", CultureInfo.InvariantCulture) + member.ToString("D6", CultureInfo.InvariantCulture) : null);
        }
    }
    private IEnumerable<TransactionEvent> Anomalies(string scenario)
    {
        switch (scenario)
        {
            case "large":
                yield return Event("large", TransactionType.Deposit, 60_001);
                break;
            case "velocity":
                for (var i = 0; i < 6; i++) yield return Event("fast", TransactionType.Deposit, 25);
                break;
            case "shared-account":
                for (var i = 0; i < 3; i++) yield return Event($"shared-{i}", TransactionType.Withdrawal, 50, account: "80001234567890", holder: "Sample Receiver");
                for (var i = 0; i < 3; i++) yield return Event($"suffix-{i}", TransactionType.Withdrawal, 50, account: $"{9000 + i}567890", holder: "Sample Receiver");
                break;
            case "bonus":
                yield return Event("bonus", TransactionType.Deposit, 200);
                yield return Event("bonus", TransactionType.BonusGrant, 500);
                yield return Event("bonus", TransactionType.BonusConversion, 100);
                break;
        }
    }
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine("Synthetic traffic started: scenario={0}, seed={1}, normalRate={2}/s", options.Scenario, options.Seed, options.Rate);
        for (var second = 0; second < options.Duration; second++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scenario = options.Scenario == "all" ? new[] { "large", "velocity", "shared-account", "bonus" }[second % 4] : options.Scenario;
            var batch = Normal().Concat(Anomalies(scenario)).ToArray();
            var tickAlerts = 0; var sentBeforeTick = sent;
            try
            {
                // Scenario events are additional to the requested normal traffic rate.
                foreach (var chunk in batch.Chunk(500))
                {
                    using var response = await client.PostAsJsonAsync("/api/events/transactions/batch", chunk, json, cancellationToken);
                    if (!response.IsSuccessStatusCode) { failures += chunk.Length; Console.Error.WriteLine("Batch rejected: HTTP {0}", (int)response.StatusCode); continue; }
                    using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    sent += chunk.Length;
                    tickAlerts += data.RootElement.EnumerateArray().Sum(x => x.GetProperty("alerts").GetArrayLength());
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            { failures += batch.Length; Console.Error.WriteLine("Batch transport failed."); }
            alerts += tickAlerts;
            Console.WriteLine("second={0} sent={1} alertHits={2} totalSent={3} failures={4}", second + 1, sent - sentBeforeTick, tickAlerts, sent, failures);
            var remaining = TimeSpan.FromSeconds(second + 1) - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, clock, cancellationToken);
        }
        Console.WriteLine("Summary: sent={0}, alertHits={1}, failures={2}, elapsed={3:F1}s", sent, alerts, failures, stopwatch.Elapsed.TotalSeconds);
        if (failures > 0) Environment.ExitCode = 1;
    }
}
