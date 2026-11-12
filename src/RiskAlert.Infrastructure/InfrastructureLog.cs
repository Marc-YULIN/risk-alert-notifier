using Microsoft.Extensions.Logging;

namespace RiskAlert.Infrastructure;

internal static partial class InfrastructureLog
{
    [LoggerMessage(1, LogLevel.Error, "Rule {RuleCode} failed with {ErrorType}; processing continues")]
    public static partial void RuleFailed(ILogger logger, string ruleCode, string errorType);
    [LoggerMessage(2, LogLevel.Warning, "Broadcast failed with {ErrorType}; alert remains persisted")]
    public static partial void BroadcastFailed(ILogger logger, string errorType);
    [LoggerMessage(3, LogLevel.Information, "Dry-run notification {NotificationId} recorded; content is available on the dashboard")]
    public static partial void DryRun(ILogger logger, Guid notificationId);
    [LoggerMessage(4, LogLevel.Error, "Delivery worker failed with {ErrorType}; pending delivery will resume on restart")]
    public static partial void WorkerFailed(ILogger logger, string errorType);
    [LoggerMessage(5, LogLevel.Warning, "Notification {NotificationId} attempt {Attempt} failed")]
    public static partial void DeliveryFailed(ILogger logger, Guid notificationId, int attempt);
}
