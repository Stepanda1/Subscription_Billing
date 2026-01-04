namespace Subscription_Backend.Dtos;

public class CreateSubscriptionRequest
{
    public int UserId { get; set; }
    public string Plan { get; set; } = null!;
    public decimal Amount { get; set; }
}
