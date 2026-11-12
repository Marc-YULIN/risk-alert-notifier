using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RiskAlert.Core;

namespace RiskAlert.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRiskAlertInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<RiskOptions>().BindConfiguration("Risk").ValidateDataAnnotations()
            .Validate(x => RuleParameters.Codes.All(code => x.Rules.TryGetValue(code, out var json) && RuleParameters.Valid(code, json)), "Invalid rule defaults.").ValidateOnStart();
        services.AddOptions<NotificationOptions>().BindConfiguration("Notifications").ValidateDataAnnotations()
            .Validate(x => TimeZoneInfo.TryFindSystemTimeZoneById(x.DisplayTimeZone, out _), "Unknown display time zone.")
            .Validate(x => string.IsNullOrEmpty(x.Telegram.BotToken) || x.Telegram.BotToken.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-'), "Invalid token format.").ValidateOnStart();
        services.AddOptions<DashboardOptions>().BindConfiguration("Dashboard").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SecurityOptions>().BindConfiguration("Security").ValidateDataAnnotations().ValidateOnStart();
        services.AddDbContext<RiskAlertDbContext>(options => options.UseSqlite(configuration.GetConnectionString("RiskAlert") ?? "Data Source=appdata/risk-alert-notifier.db"));
        services.AddSingleton<IngestGate>();
        services.AddSingleton<RuleSettingsCache>();
        services.AddScoped<RuleSettingsProvider>();
        services.AddScoped<ITransactionHistory, EfTransactionHistory>();
        services.AddScoped<RuleEngine>();
        services.AddSingleton<IRiskRule, LargeTransactionRule>();
        services.AddSingleton<IRiskRule, VelocityRule>();
        services.AddSingleton<IRiskRule, SharedPayoutAccountRule>();
        services.AddSingleton<IRiskRule, BonusRatioRule>();
        services.AddHttpClient<TelegramNotifier>(client => client.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();
        services.AddSingleton<DryRunNotifier>();
        services.AddSingleton<INotificationDelay, NotificationDelay>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddHostedService(provider => provider.GetRequiredService<NotificationDispatcher>());
        return services;
    }
    public static async Task InitializeRiskAlertAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RiskAlertDbContext>();
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.Database.GetConnectionString());
        if (builder.DataSource != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (directory is not null) Directory.CreateDirectory(directory);
        }
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<RuleSettingsProvider>().GetAsync(cancellationToken);
        if (!await db.NotificationSettings.AnyAsync(cancellationToken))
        {
            db.NotificationSettings.Add(new NotificationSetting());
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
