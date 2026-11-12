#pragma warning disable CA1707 // Scenario-based test names use underscores.
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using RiskAlert.Core;
using RiskAlert.Infrastructure;

namespace RiskAlert.Tests;

internal sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly string database = Path.Combine(Path.GetTempPath(), "risk-api-" + Guid.NewGuid() + ".db");
    public IAlertBroadcaster Broadcaster { get; } = Substitute.For<IAlertBroadcaster>();
    public ManualTimeProvider Clock { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing").UseSetting("ConnectionStrings:RiskAlert", "Data Source=" + database)
            .UseSetting("Notifications:Telegram:BotToken", "");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAlertBroadcaster>(); services.AddSingleton(Broadcaster);
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
        });
    }
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(database + suffix)) File.Delete(database + suffix);
    }
}
public sealed class ApiTests
{
    private static StringContent Payload(TestApiFactory factory, string id = "large-1", decimal amount = 60001, string type = "Deposit", string? account = null) =>
        new(JsonSerializer.Serialize(new { eventId = id, memberId = "member-1", type, amount, currency = "USD", occurredAt = factory.Clock.GetUtcNow(),
            payoutBankCode = "DemoBank", payoutAccountNo = account, payoutAccountHolder = "Sample Receiver" }), Encoding.UTF8, "application/json");
    [Fact]
    public async Task IngestAsync_LargeAndDuplicate_PersistsBroadcastsAndDeduplicates()
    {
        await using var factory = new TestApiFactory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Api-Key", "dev-ingest-key");
        using var response = await client.PostAsync("/api/events/transactions", Payload(factory)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var raised = Assert.Single(result.RootElement.GetProperty("alerts").EnumerateArray()); Assert.True(raised.GetProperty("isNew").GetBoolean());
        var id = raised.GetProperty("id").GetGuid();
        await factory.Broadcaster.Received(1).BroadcastAsync(Arg.Is<Alert>(a => a.Id == id && a.Severity == Severity.Critical), true, Arg.Any<CancellationToken>());
        using var duplicate = await client.PostAsync("/api/events/transactions", Payload(factory));
        using var duplicateResult = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync()); Assert.True(duplicateResult.RootElement.GetProperty("duplicate").GetBoolean());
        using var listing = JsonDocument.Parse(await client.GetStringAsync("/api/alerts?currency=USD&severity=Critical")); Assert.Equal(1, listing.RootElement.GetProperty("total").GetInt32());
        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/alerts/{id}")); Assert.Single(detail.RootElement.GetProperty("sourceEvents").EnumerateArray());
        using var acknowledge = await client.PostAsJsonAsync($"/api/alerts/{id}/acknowledge", new { by = "operator" }); Assert.Equal(HttpStatusCode.OK, acknowledge.StatusCode);
        using var resolved = await client.PostAsJsonAsync($"/api/alerts/{id}/resolve", new { by = "operator" }); Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        using var invalid = await client.PostAsJsonAsync($"/api/alerts/{id}/acknowledge", new { by = "operator" }); Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
    }
    [Fact]
    public async Task IngestAsync_MissingApiKey_ReturnsUnauthorizedProblem()
    {
        await using var factory = new TestApiFactory(); using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/events/transactions", Payload(factory)); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal("InvalidApiKey", body.RootElement.GetProperty("code").GetString());
    }
    [Fact]
    public async Task UpdateAsync_DisabledRule_InvalidatesCachedSettings()
    {
        await using var factory = new TestApiFactory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Api-Key", "dev-ingest-key");
        using var first = await client.PostAsync("/api/events/transactions", Payload(factory)); first.EnsureSuccessStatusCode();
        using var disabled = await client.PutAsJsonAsync("/api/rules/LARGE_TXN", new { enabled = false, parameters = new { } }); Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        using var second = await client.PostAsync("/api/events/transactions", Payload(factory, "large-2"));
        using var result = JsonDocument.Parse(await second.Content.ReadAsStringAsync()); Assert.Empty(result.RootElement.GetProperty("alerts").EnumerateArray());
        using var invalid = await client.PutAsJsonAsync("/api/rules/VELOCITY", new { enabled = true, parameters = new { windowMinutes = 0 } }); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    [Fact]
    public async Task IngestAsync_ConcurrentDuplicate_CreatesOneEvent()
    {
        await using var factory = new TestApiFactory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Api-Key", "dev-ingest-key");
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsync("/api/events/transactions", Payload(factory))));
        foreach (var response in responses) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); response.Dispose(); }
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<RiskAlertDbContext>();
        Assert.Equal(1, await db.Transactions.CountAsync()); Assert.Equal(1, await db.Alerts.CountAsync());
    }
    [Fact]
    public async Task IngestAsync_Withdrawal_StoresOnlyProtectedAccountAndExactMoney()
    {
        await using var factory = new TestApiFactory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Api-Key", "dev-ingest-key");
        using var response = await client.PostAsync("/api/events/transactions", Payload(factory, amount: 60000.1234m, type: "Withdrawal", account: "123456789012")); response.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<RiskAlertDbContext>();
        var transaction = await db.Transactions.SingleAsync(); Assert.Equal("****789012", transaction.PayoutAccountNo); Assert.NotEqual("123456789012", transaction.PayoutAccountHash);
        Assert.Equal(60000.1234m, transaction.Amount); Assert.Equal(60000.1234m, await db.Transactions.SumAsync(x => x.Amount));
        var alertId = await db.Alerts.Select(x => x.Id).SingleAsync(); var detail = await client.GetStringAsync($"/api/alerts/{alertId}");
        Assert.DoesNotContain("123456789012", detail); Assert.DoesNotContain(transaction.PayoutAccountHash!, detail);
    }
    [Fact]
    public async Task IngestAsync_InvalidBatchOrAmount_ReturnsProblemWithoutPersisting()
    {
        await using var factory = new TestApiFactory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Api-Key", "dev-ingest-key");
        using var response = await client.PostAsync("/api/events/transactions", Payload(factory, amount: .00001m)); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var batch = await client.PostAsJsonAsync("/api/events/transactions/batch", Array.Empty<object>()); Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        using var scope = factory.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<RiskAlertDbContext>().Transactions.ToArrayAsync());
    }
}
