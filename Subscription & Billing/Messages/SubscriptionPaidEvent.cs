namespace Subscription_Backend.Messages;

public record SubscriptionPaidEvent
{
    public int SubscriptionId { get; init; }
    public decimal Amount { get; init; }
    public DateTime PaidAt { get; init; }
}