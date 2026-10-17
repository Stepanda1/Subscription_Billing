using Microsoft.AspNetCore.Hosting;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Subscription_Backend;
using Subscription_Backend.Models;
using Xunit;

namespace Billing.Tests;

public sealed class FailBeforeCommit : SaveChangesInterceptor
{
    public bool Enabled { get; set; }
    public Guid FailedMessageId { get; private set; }
    public CancellationTokenSource? CancelAfterPayment { get; set; }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken ct = default)
    {
        if (CancelAfterPayment is { } cancellation &&
            eventData.Context!.ChangeTracker.Entries<Payment>().Any() &&
            !eventData.Context.ChangeTracker.Entries<IdempotencyRecord>().Any())
            cancellation.Cancel();
        return ValueTask.FromResult(result);
    }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (Enabled && eventData.Context!.ChangeTracker.Entries<IdempotencyRecord>()
                .Any(x => x.State == EntityState.Added))
        {
            FailedMessageId = eventData.Context.ChangeTracker.Entries<OutboxMessage>().Single().Entity.MessageId;
            throw new InvalidOperationException("Injected failure before outbox/response commit.");
        }
        return ValueTask.FromResult(result);
    }
}

// Models an acknowledgement lost after PostgreSQL has committed the request.
public sealed class FailAfterCommit : DbTransactionInterceptor
{
    public bool Enabled { get; set; }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken ct = default)
    {
        if (Enabled && eventData.Context!.ChangeTracker.Entries<IdempotencyRecord>().Any())
        {
            Enabled = false;
            throw new InvalidOperationException("Injected failure after successful commit.");
        }
        return Task.CompletedTask;
    }
}

public sealed class BillingFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public FailBeforeCommit Fault { get; } = new();
    public FailAfterCommit CommitFault { get; } = new();
    public ActivationFault ActivationFault { get; } = new();
    public PaymentObservations Observations { get; } = new();
    public string QueueName { get; } = $"billing-tests-activation-{Guid.NewGuid():N}";
    private HttpClient rabbitAdmin = null!;
    private ConnectHandle? observationHandle;
    public HttpClient Client { get; private set; } = null!;
    private string connection = null!;

    public async Task InitializeAsync()
    {
        connection = Environment.GetEnvironmentVariable("TEST_DATABASE")
            ?? throw new InvalidOperationException("Run tests with docker compose --profile test run --build --rm tests.");
        // A separate database and message exchange keep demo data isolated.
        if (new Npgsql.NpgsqlConnectionStringBuilder(connection).Database != "billing_tests")
            throw new InvalidOperationException("TEST_DATABASE must target the disposable billing_tests database.");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connection);
        Environment.SetEnvironmentVariable("RabbitMq__Host", Environment.GetEnvironmentVariable("TEST_RABBIT_HOST") ?? "localhost");
        Environment.SetEnvironmentVariable("RabbitMq__Password", Environment.GetEnvironmentVariable("TEST_RABBIT_PASSWORD") ?? "local-broker-only");
        Environment.SetEnvironmentVariable("RabbitMq__ActivationQueue", QueueName);
        Environment.SetEnvironmentVariable("ServiceRole", "All");
        Environment.SetEnvironmentVariable("Api__ConcurrencyLimit", "24");
        rabbitAdmin = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("TEST_RABBIT_MANAGEMENT_URL") ??
                $"http://{Environment.GetEnvironmentVariable("TEST_RABBIT_HOST") ?? "localhost"}:15672"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"billing:{Environment.GetEnvironmentVariable("TEST_RABBIT_PASSWORD") ?? "local-broker-only"}"));
        rabbitAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        Client = CreateClient();
        observationHandle = Services.GetRequiredService<IBus>().ConnectConsumeObserver(Observations);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var result = await Client.GetAsync("/health/ready");
            if (result.IsSuccessStatusCode) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("Broker/database did not become ready.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection).AddInterceptors(Fault, CommitFault, ActivationFault));
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        observationHandle?.Disconnect();
        Client.Dispose();
        await DisposeAsync();
        // A unique topology prevents stale broker messages from hitting a reset test DB.
        // Remove only resources named by this fixture, after its bus has stopped.
        foreach (var name in new[] { QueueName, QueueName + "_error", QueueName + "_skipped" })
        {
            using var queue = await rabbitAdmin.DeleteAsync($"/api/queues/%2F/{name}");
            if (queue.StatusCode != HttpStatusCode.NotFound) queue.EnsureSuccessStatusCode();
            using var exchange = await rabbitAdmin.DeleteAsync($"/api/exchanges/%2F/{name}");
            if (exchange.StatusCode != HttpStatusCode.NotFound) exchange.EnsureSuccessStatusCode();
        }
        using var paid = await rabbitAdmin.DeleteAsync($"/api/exchanges/%2F/{QueueName}-paid");
        if (paid.StatusCode != HttpStatusCode.NotFound) paid.EnsureSuccessStatusCode();
        rabbitAdmin.Dispose();
    }

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<JsonElement> WaitForError(Guid messageId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await rabbitAdmin.PostAsJsonAsync($"/api/queues/%2F/{QueueName}_error/get",
                new { count = 100, ackmode = "ack_requeue_false", encoding = "auto" });
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                var messages = await response.Content.ReadFromJsonAsync<JsonElement[]>();
                foreach (var message in messages!)
                {
                    using var payload = JsonDocument.Parse(message.GetProperty("payload").GetString()!);
                    if (payload.RootElement.GetProperty("messageId").GetGuid() == messageId) return message;
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Message {messageId} did not reach the error queue.");
    }
}
