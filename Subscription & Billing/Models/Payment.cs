namespace Subscription_Backend.Models;

public class Payment
{
    public int Id { get; set; }

    public int SubscriptionId { get; set; }
    public Subscription Subscription { get; set; } = null!;

    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
}