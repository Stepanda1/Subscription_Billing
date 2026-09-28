using MassTransit;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Messages;

namespace Subscription_Backend.Consumers;

public sealed class SubscriptionActivationConsumer(AppDbContext db,
    ILogger<SubscriptionActivationConsumer> logger) : IConsumer<SubscriptionPaidEvent>
{
    public async Task Consume(ConsumeContext<SubscriptionPaidEvent> context)
    {
        var id = context.Message.SubscriptionId;
        if (!await db.Payments.AnyAsync(x => x.SubscriptionId == id && x.Amount == context.Message.Amount,
                context.CancellationToken))
            throw new InvalidOperationException($"Payment for subscription {id} is missing or inconsistent.");
        // Also safe for different message IDs carrying the same business event.
        var changed = await db.Subscriptions.Where(x => x.Id == id && !x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true), context.CancellationToken);
        logger.LogInformation("Activation processed for {SubscriptionId}; changed {Changed}", id, changed);
    }
}
