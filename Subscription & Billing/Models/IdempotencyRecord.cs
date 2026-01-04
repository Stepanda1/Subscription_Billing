using System.ComponentModel.DataAnnotations;

namespace Subscription_Backend.Models;

public class IdempotencyRecord
{
    [Key]
    public Guid Key { get; set; }

    public string Endpoint { get; set; } = null!;
    public string Response { get; set; } = null!;
    public int StatusCode { get; set; }
    public DateTime CreatedAt { get; set; }
}