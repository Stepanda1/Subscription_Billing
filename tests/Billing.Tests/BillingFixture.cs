using Microsoft.AspNetCore.Hosting;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

public sealed class BillingFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public FailBeforeCommit Fault { get; } = new();
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
        Environment.SetEnvironmentVariable("RabbitMq__ActivationQueue", "billing-tests-activation");
        Environment.SetEnvironmentVariable("ServiceRole", "All");
        Environment.SetEnvironmentVariable("Api__ConcurrencyLimit", "24");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        Client = CreateClient();
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
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection).AddInterceptors(Fault));
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        Client.Dispose();
        await DisposeAsync();
    }

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
