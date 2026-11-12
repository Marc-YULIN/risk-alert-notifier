using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RiskAlert.Core;
using RiskAlert.Infrastructure;

namespace RiskAlert.Api.Endpoints;

public static class ApiProblems
{
    public static IResult Error(int status, string code, string title) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
public static class EventEndpoints
{
    public static RouteGroupBuilder MapEvents(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events").WithTags("Events");
        group.AddEndpointFilter(async (context, next) =>
        {
            var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.IngestApiKey;
            var actual = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual))))
                return ApiProblems.Error(401, "InvalidApiKey", "A valid ingestion API key is required.");
            return await next(context);
        });
        group.MapPost("/transactions", async (TransactionEvent input, RuleEngine engine, CancellationToken cancellationToken) =>
            Valid(input) ? Results.Ok(await engine.ProcessAsync(input, cancellationToken)) : ApiProblems.Error(400, "InvalidEvent", "Invalid transaction event."))
            .WithSummary("Ingest one idempotent transaction event");
        group.MapPost("/transactions/batch", async (TransactionEvent[] inputs, RuleEngine engine, CancellationToken cancellationToken) =>
        {
            if (inputs.Length is < 1 or > 500 || inputs.Any(x => x is null || !Valid(x)))
                return ApiProblems.Error(400, "InvalidBatch", "Supply 1 to 500 valid events.");
            var results = new List<ProcessResult>();
            foreach (var input in inputs) results.Add(await engine.ProcessAsync(input, cancellationToken));
            return Results.Ok(results);
        }).WithSummary("Ingest up to 500 events sequentially");
        return group;
    }
    public static bool Valid(TransactionEvent e) => !string.IsNullOrWhiteSpace(e.EventId) && e.EventId.Length <= 100 &&
        !string.IsNullOrWhiteSpace(e.MemberId) && e.MemberId.Length <= 100 && (e.MemberName?.Length ?? 0) <= 100 &&
        Enum.IsDefined(e.Type) && e.Amount > 0 && e.Amount <= 100_000_000_000_000m && decimal.Round(e.Amount, 4) == e.Amount &&
        e.Currency is not null && RuleParameters.CurrencyValid(e.Currency) && e.OccurredAt != default &&
        e.OccurredAt.Year is >= 2000 and <= 2100 && (e.PayoutAccountNo?.Length ?? 0) <= 100 &&
        (e.PayoutBankCode?.Length ?? 0) <= 40 && (e.PayoutAccountHolder?.Length ?? 0) <= 100 &&
        (e.Type != TransactionType.Withdrawal || (!string.IsNullOrWhiteSpace(e.PayoutBankCode) && PayoutPrivacy.Digits(e.PayoutAccountNo).Length >= 6));
}
