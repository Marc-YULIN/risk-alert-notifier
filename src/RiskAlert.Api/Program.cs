using System.Text.Json.Serialization;
using RiskAlert.Api.Endpoints;
using RiskAlert.Api.Hubs;
using RiskAlert.Core;
using RiskAlert.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("code", "RequestFailed"));
builder.Services.AddExceptionHandler(options => options.ExceptionHandler = async context =>
    await Results.Problem(statusCode: 500, title: "An unexpected error occurred.",
        extensions: new Dictionary<string, object?> { ["code"] = "InternalError" }).ExecuteAsync(context));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks();
builder.Services.AddRiskAlertInfrastructure(builder.Configuration);
builder.Services.AddScoped<IAlertBroadcaster, SignalRAlertBroadcaster>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health");
app.MapHub<AlertsHub>("/hubs/alerts");
app.MapEvents();
app.MapAlerts();
app.MapRules();
app.MapNotifications();
app.MapStats();
app.MapLocalization();
await app.Services.InitializeRiskAlertAsync(app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program;
