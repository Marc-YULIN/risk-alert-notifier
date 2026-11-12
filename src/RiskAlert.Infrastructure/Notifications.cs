using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RiskAlert.Core;

namespace RiskAlert.Infrastructure;

public sealed class TelegramDeliveryException(TimeSpan? retryAfter = null) : Exception("Telegram delivery failed.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
public interface INotificationDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}
public sealed class NotificationDelay(TimeProvider timeProvider) : INotificationDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, timeProvider, cancellationToken);
}
public sealed class TelegramNotifier(HttpClient client, IOptions<NotificationOptions> options)
{
    public async Task SendAsync(string text, string? chatOverride, CancellationToken cancellationToken)
    {
        var telegram = options.Value.Telegram;
        var chatId = string.IsNullOrWhiteSpace(chatOverride) ? telegram.ChatId : chatOverride;
        if (string.IsNullOrWhiteSpace(chatId)) throw new TelegramDeliveryException();
        try
        {
            using var response = await client.PostAsJsonAsync(new Uri($"https://api.telegram.org/bot{telegram.BotToken}/sendMessage"),
                new { chat_id = chatId, text, parse_mode = "MarkdownV2", disable_web_page_preview = true }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var seconds = body.RootElement.TryGetProperty("parameters", out var parameters) &&
                    parameters.TryGetProperty("retry_after", out var retry) && retry.TryGetInt32(out var value) ? Math.Max(1, value) : 2;
                throw new TelegramDeliveryException(TimeSpan.FromSeconds(seconds));
            }
            if (!response.IsSuccessStatusCode) throw new TelegramDeliveryException();
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!result.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new TelegramDeliveryException();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        { throw new TelegramDeliveryException(); }
    }
}
public sealed class DryRunNotifier(ILogger<DryRunNotifier> logger)
{
    public Task SendAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InfrastructureLog.DryRun(logger, notificationId);
        return Task.CompletedTask;
    }
}

public sealed class NotificationDispatcher(IServiceScopeFactory scopeFactory, IOptions<NotificationOptions> options,
    IOptions<DashboardOptions> dashboard, TimeProvider timeProvider, INotificationDelay delay,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public void Enqueue(Guid id) => queue.Writer.TryWrite(id);
    public async Task<OutboundNotification> PrepareAsync(RiskAlertDbContext db, Alert alert, bool isNew,
        Severity? previousSeverity, CancellationToken cancellationToken)
    {
        var settings = await db.NotificationSettings.AsNoTracking().ToArrayAsync(cancellationToken);
        var decision = NotificationPolicy.ShouldSend(alert, settings, isNew, previousSeverity);
        var rendered = AlertTextCatalog.Render(alert, options.Value.Telegram.Locale);
        var emoji = alert.Severity switch { Severity.Low => "⚪", Severity.Medium => "🟡", Severity.High => "🟠", _ => "🔴" };
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.DisplayTimeZone);
        var link = dashboard.Value.PublicUrl.TrimEnd('/') + "/?alert=" + alert.Id +
            "&from=" + Uri.EscapeDataString(alert.WindowStart.ToString("O", CultureInfo.InvariantCulture)) +
            "&to=" + Uri.EscapeDataString(alert.WindowEnd.ToString("O", CultureInfo.InvariantCulture));
        var utc = alert.LastSeenAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        var local = TimeZoneInfo.ConvertTime(alert.LastSeenAt, zone).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var text = TelegramMarkdown.Escape($"{emoji} {rendered.Title}\n{rendered.Body}\n{utc}\n{local} ({zone.Id})\n{link}");
        var outbound = new OutboundNotification { AlertId = alert.Id, Text = text,
            Channel = string.IsNullOrWhiteSpace(options.Value.Telegram.BotToken) ? NotificationChannel.DryRun : NotificationChannel.Telegram,
            Status = decision.Send ? DeliveryStatus.Pending : DeliveryStatus.Skipped, SkipReason = decision.Reason };
        db.Notifications.Add(outbound);
        return outbound;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup initialization completes before the hosted worker begins. Pending rows survive restarts.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RiskAlertDbContext>();
            foreach (var id in await db.Notifications.Where(x => x.Status == DeliveryStatus.Pending).Select(x => x.Id).ToArrayAsync(stoppingToken)) Enqueue(id);
        }
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await DeliverAsync(id, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { InfrastructureLog.WorkerFailed(logger, ex.GetType().Name); }
        }
    }
    public async Task DeliverAsync(Guid id, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<RiskAlertDbContext>();
        var outbound = await db.Notifications.FindAsync([id], cancellationToken);
        if (outbound is null || outbound.Status != DeliveryStatus.Pending) return;
        var alert = await db.Alerts.SingleAsync(x => x.Id == outbound.AlertId, cancellationToken);
        var settings = await db.NotificationSettings.AsNoTracking().ToArrayAsync(cancellationToken);
        var destination = settings.FirstOrDefault(x => x.Currency == alert.Currency) ?? settings.FirstOrDefault(x => x.Currency == "*");
        while (outbound.Attempts < 4)
        {
            outbound.Attempts++;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                if (outbound.Channel == NotificationChannel.DryRun)
                    await services.GetRequiredService<DryRunNotifier>().SendAsync(id, cancellationToken);
                else await services.GetRequiredService<TelegramNotifier>().SendAsync(outbound.Text, destination?.ChatIdOverride, cancellationToken);
                outbound.Status = DeliveryStatus.Sent; outbound.SentAt = timeProvider.GetUtcNow(); outbound.Error = null;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                outbound.Error = "Delivery failed";
                InfrastructureLog.DeliveryFailed(logger, id, outbound.Attempts);
                if (outbound.Attempts >= 4) break;
                var retryAfter = (ex as TelegramDeliveryException)?.RetryAfter ?? TimeSpan.FromSeconds(1 << outbound.Attempts);
                await db.SaveChangesAsync(cancellationToken);
                await delay.WaitAsync(retryAfter, cancellationToken);
            }
        }
        outbound.Status = DeliveryStatus.Failed;
        await db.SaveChangesAsync(cancellationToken);
    }
}
