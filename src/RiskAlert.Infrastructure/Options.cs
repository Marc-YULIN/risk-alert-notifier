using System.ComponentModel.DataAnnotations;
using RiskAlert.Core;

namespace RiskAlert.Infrastructure;

public sealed class RiskOptions
{
    [Range(1, 1440)] public int DedupeWindowMinutes { get; set; } = 30;
    public Dictionary<string, string> Rules { get; set; } = RuleParameters.Codes.ToDictionary(x => x, RuleParameters.Defaults);
}
public sealed class NotificationOptions
{
    [Required] public string DisplayTimeZone { get; set; } = "Asia/Taipei";
    public TelegramOptions Telegram { get; set; } = new();
}
public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string Locale { get; set; } = "en";
}
public sealed class DashboardOptions
{
    [Required, Url] public string PublicUrl { get; set; } = "http://localhost:5107";
}
public sealed class SecurityOptions
{
    [Required, MinLength(12)] public string IngestApiKey { get; set; } = "dev-ingest-key";
}
