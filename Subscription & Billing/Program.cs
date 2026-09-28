using System.Threading.RateLimiting;
using MassTransit;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend;
using Subscription_Backend.Consumers;
using Subscription_Backend.Infrastructure;
using Subscription_Backend.Services;

var builder = WebApplication.CreateBuilder(args);
var role = builder.Configuration["ServiceRole"] ?? "All";
if (role is not ("Api" or "Worker" or "All" or "Migrate"))
    throw new InvalidOperationException("ServiceRole must be Api, Worker, All or Migrate.");
var connection = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings__DefaultConnection is required.");
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));

if (role == "Migrate")
{
    var migrationApp = builder.Build();
    await using var scope = migrationApp.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    await migrationApp.DisposeAsync();
    return;
}

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<BillingExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, _) => await Results.Problem(
        statusCode: 429, detail: "Too many concurrent requests. Retry with backoff and the same idempotency key.")
        .ExecuteAsync(context.HttpContext);
    o.AddConcurrencyLimiter("api", options =>
    {
        options.PermitLimit = builder.Configuration.GetValue("Api:ConcurrencyLimit", 64);
        options.QueueLimit = 0;
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);
builder.Services.Configure<MassTransitHostOptions>(o =>
{
    // The bus outbox lets HTTP writes succeed while the broker is unavailable.
    o.WaitUntilStarted = false;
    o.StopTimeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddMassTransit(x =>
{
    if (role is "Worker" or "All") x.AddConsumer<SubscriptionActivationConsumer>();
    x.AddEntityFrameworkOutbox<AppDbContext>(o =>
    {
        o.UsePostgres();
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.UseBusOutbox();
    });
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "billing");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? throw new InvalidOperationException("RabbitMq__Password is required."));
        });
        // Declare the durable destination even when API starts before a worker.
        var queue = builder.Configuration["RabbitMq:ActivationQueue"] ?? "subscription-activation";
        cfg.Message<Subscription_Backend.Messages.SubscriptionPaidEvent>(m => m.SetEntityName(queue + "-paid"));
        cfg.Publish<Subscription_Backend.Messages.SubscriptionPaidEvent>(p => p.BindQueue(queue, queue));
        if (role is "Worker" or "All")
            cfg.ReceiveEndpoint(builder.Configuration["RabbitMq:ActivationQueue"] ?? "subscription-activation", e =>
            {
                e.PrefetchCount = 32;
                e.ConcurrentMessageLimit = 8;
                e.UseMessageRetry(r => r.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)));
                e.UseEntityFrameworkOutbox<AppDbContext>(context);
                e.ConfigureConsumer<SubscriptionActivationConsumer>(context);
            });
    });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseRateLimiter();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready") || check.Tags.Contains("masstransit")
});
if (role is "Api" or "All")
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapControllers().RequireRateLimiting("api");
}
app.Run();

public partial class Program;
