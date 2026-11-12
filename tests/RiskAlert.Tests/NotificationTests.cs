#pragma warning disable CA1707 // Scenario-based test names use underscores.
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RiskAlert.Core;
using RiskAlert.Infrastructure;

namespace RiskAlert.Tests;

internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
internal sealed class RecordingDelay : INotificationDelay
{
    public List<TimeSpan> Delays { get; } = [];
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) { Delays.Add(delay); return Task.CompletedTask; }
}
internal sealed class RecordingLogs : ILoggerProvider
{
    public List<string> Messages { get; } = [];
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
    public void Dispose() { }
    private sealed class CaptureLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (messages) messages.Add(formatter(state, exception) + exception?.ToString()); }
    }
}
public sealed class NotificationTests
{
    private const string Token = "123456:sample-secret-token";
    [Fact]
    public async Task SendAsync_ValidRequest_UsesRequiredBodyAndNeverLogsToken()
    {
        var logs = new RecordingLogs();
        using var handler = new StubHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://api.telegram.org/bot{Token}/sendMessage", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("override", body.RootElement.GetProperty("chat_id").GetString());
            Assert.Equal("safe\\_text", body.RootElement.GetProperty("text").GetString());
            Assert.Equal("MarkdownV2", body.RootElement.GetProperty("parse_mode").GetString());
            Assert.True(body.RootElement.GetProperty("disable_web_page_preview").GetBoolean());
            return Response(HttpStatusCode.OK, "{\"ok\":true}");
        });
        await using var provider = Services(handler, logs, new RecordingDelay());
        await provider.GetRequiredService<TelegramNotifier>().SendAsync("safe\\_text", "override", CancellationToken.None);
        Assert.DoesNotContain(logs.Messages, x => x.Contains(Token, StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task DeliverAsync_TransientOrPermanentFailure_RetriesAndPersistsOutcome(bool rateLimited)
    {
        var requests = 0; var logs = new RecordingLogs(); var delays = new RecordingDelay();
        using var handler = new StubHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(rateLimited ? (requests == 1 ? Response(HttpStatusCode.TooManyRequests, "{\"parameters\":{\"retry_after\":7}}") : Response(HttpStatusCode.OK, "{\"ok\":true}")) :
                Response(HttpStatusCode.InternalServerError, Token));
        });
        await using var provider = Services(handler, logs, delays);
        using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<RiskAlertDbContext>();
        await db.Database.EnsureCreatedAsync();
        var clock = new ManualTimeProvider(); var alert = AlertDeduplicator.MergeOrCreate(Samples.Hit(clock), [], clock.GetUtcNow(), 30);
        db.Alerts.Add(alert); db.NotificationSettings.Add(new NotificationSetting()); await db.SaveChangesAsync();
        var dispatcher = provider.GetRequiredService<NotificationDispatcher>();
        var outbound = await dispatcher.PrepareAsync(db, alert, true, null, CancellationToken.None); await db.SaveChangesAsync();
        Assert.Contains("UTC", outbound.Text); Assert.Contains("Asia/Taipei", outbound.Text); Assert.Contains("from\\=", outbound.Text);
        await dispatcher.DeliverAsync(outbound.Id, CancellationToken.None);
        db.ChangeTracker.Clear(); var result = await db.Notifications.SingleAsync();
        Assert.Equal(rateLimited ? DeliveryStatus.Sent : DeliveryStatus.Failed, result.Status);
        Assert.Equal(rateLimited ? 2 : 4, result.Attempts); Assert.Equal(result.Attempts, requests);
        Assert.Equal(rateLimited ? [TimeSpan.FromSeconds(7)] : new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) }, delays.Delays);
        Assert.DoesNotContain(logs.Messages, x => x.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(Token, result.Error ?? "");
        await dispatcher.DeliverAsync(outbound.Id, CancellationToken.None); Assert.Equal(result.Attempts, requests);
    }
    [Fact]
    public async Task PrepareAsync_DisabledCurrency_PersistsSkippedRecord()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No request expected."));
        await using var provider = Services(handler, new RecordingLogs(), new RecordingDelay());
        using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<RiskAlertDbContext>(); await db.Database.EnsureCreatedAsync();
        db.NotificationSettings.Add(new NotificationSetting { Currency = "USD", Enabled = false }); await db.SaveChangesAsync();
        var clock = new ManualTimeProvider(); var alert = AlertDeduplicator.MergeOrCreate(Samples.Hit(clock), [], clock.GetUtcNow(), 30);
        var result = await provider.GetRequiredService<NotificationDispatcher>().PrepareAsync(db, alert, true, null, CancellationToken.None);
        Assert.Equal(DeliveryStatus.Skipped, result.Status); Assert.Equal("CurrencyDisabled", result.SkipReason);
    }
    private static ServiceProvider Services(StubHandler handler, RecordingLogs logs, RecordingDelay delay)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RiskAlert"] = "Data Source=" + Path.Combine(Path.GetTempPath(), "risk-test-" + Guid.NewGuid() + ".db"),
            ["Notifications:Telegram:BotToken"] = Token, ["Notifications:Telegram:ChatId"] = "default"
        }).Build();
        var services = new ServiceCollection(); services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddRiskAlertInfrastructure(configuration);
        services.AddHttpClient<TelegramNotifier>().ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<INotificationDelay>(delay);
        return services.BuildServiceProvider();
    }
    private static HttpResponseMessage Response(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
