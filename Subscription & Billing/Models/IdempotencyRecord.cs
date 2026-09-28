namespace Subscription_Backend.Models;

public sealed class IdempotencyRecord
{
    public Guid Key { get; set; }
    public string RequestHash { get; set; } = null!;
    public string Response { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
