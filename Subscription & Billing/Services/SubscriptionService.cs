using MassTransit;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Dtos;
using Subscription_Backend.Messages;
using Subscription_Backend.Models;
using System.Text.Json;

namespace Subscription_Backend.Services;

public class SubscriptionService
{
    private readonly AppDbContext _db;
    private readonly IPublishEndpoint _publishEndpoint;

    public SubscriptionService(AppDbContext db, IPublishEndpoint publishEndpoint)
    {
        _db = db;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Subscription> CreateSubscriptionAsync(CreateSubscriptionRequest request, Guid idempotencyKey)
    {
        var existing = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(x =>
                x.Key == idempotencyKey &&
                x.Endpoint == "POST /api/subscriptions/create");

        if (existing != null)
        {
            return JsonSerializer.Deserialize<Subscription>(existing.Response)!;
        }

        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();

            try
            {
                var user = await _db.Users.FindAsync(request.UserId);
                if (user == null)
                    throw new Exception("User not found");

                var subscription = new Subscription
                {
                    UserId = user.Id,
                    Plan = request.Plan,
                    IsActive = false
                };

                _db.Subscriptions.Add(subscription);
                await _db.SaveChangesAsync();

                _db.Payments.Add(new Payment
                {
                    SubscriptionId = subscription.Id,
                    Amount = request.Amount
                });

                await _db.SaveChangesAsync();

                _db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    Key = idempotencyKey,
                    Endpoint = "POST /api/subscriptions/create",
                    Response = JsonSerializer.Serialize(subscription),
                    StatusCode = 200,
                    CreatedAt = DateTime.UtcNow
                });

                await _db.SaveChangesAsync();

                await tx.CommitAsync();

                return subscription;
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });
    }
}