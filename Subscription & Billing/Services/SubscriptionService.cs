using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Dtos;
using Subscription_Backend.Messages;
using Subscription_Backend.Models;

namespace Subscription_Backend.Services;

public sealed class SubscriptionService(AppDbContext db, IPublishEndpoint publisher,
    ILogger<SubscriptionService> logger)
{
    public async Task<SubscriptionResponse> CreateSubscriptionAsync(
        CreateSubscriptionRequest request, Guid key, CancellationToken ct)
    {
        var amount = PlanCatalog.Price(request.Plan);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"v1:{request.UserId}:{request.Plan}")));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Works across replicas. Hash collisions only serialize unrelated requests.
        var lockId = BitConverter.ToInt64(SHA256.HashData(key.ToByteArray()), 0);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})", ct);
        var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash)
                throw new BillingException(409, "Idempotency key was used with a different request.");
            return JsonSerializer.Deserialize<SubscriptionResponse>(existing.Response)!;
        }
        if (!await db.Users.AnyAsync(x => x.Id == request.UserId, ct))
            throw new BillingException(404, "User not found.");

        var subscription = new Subscription { UserId = request.UserId, Plan = request.Plan };
        // PostgreSQL timestamps have microsecond precision. Use the same persisted
        // business timestamp in the event, rather than the later publishing time.
        var now = DateTime.UtcNow;
        var paidAt = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
        db.Subscriptions.Add(subscription);
        db.Payments.Add(new Payment { Subscription = subscription, Amount = amount, PaidAt = paidAt });
        await db.SaveChangesAsync(ct);
        var response = new SubscriptionResponse(subscription.Id, subscription.UserId, subscription.Plan, false);
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Key = key, RequestHash = hash, Response = JsonSerializer.Serialize(response), CreatedAt = DateTime.UtcNow
        });
        // Scoped publisher is intercepted by the EF bus outbox.
        // Payment, subscription, response and event commit or roll back together.
        await publisher.Publish(new SubscriptionPaidEvent
        {
            SubscriptionId = subscription.Id, Amount = amount, PaidAt = paidAt
        }, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Payment recorded for {SubscriptionId}, key {IdempotencyKey}", subscription.Id, key);
        return response;
    }
}
