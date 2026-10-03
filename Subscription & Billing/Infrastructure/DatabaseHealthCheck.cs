using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Subscription_Backend.Infrastructure;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable.");
    }
}
