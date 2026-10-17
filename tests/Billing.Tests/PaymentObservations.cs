using System.Collections.Concurrent;
using MassTransit;
using Subscription_Backend.Messages;

namespace Billing.Tests;

// Observe the actual broker delivery, not an event constructed by the test.
public sealed class PaymentObservations : IConsumeObserver
{
    public ConcurrentDictionary<int, SubscriptionPaidEvent> Events { get; } = new();
    public ConcurrentDictionary<Guid, int> Attempts { get; } = new();

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        if (context.Message is SubscriptionPaidEvent payment)
        {
            Events.TryAdd(payment.SubscriptionId, payment);
            if (context.MessageId is { } id) Attempts.AddOrUpdate(id, 1, (_, n) => n + 1);
        }
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;
    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class => Task.CompletedTask;
}
