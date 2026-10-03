using System.ComponentModel.DataAnnotations;

namespace Subscription_Backend.Dtos;

public sealed record CreateSubscriptionRequest
{
    [Range(1, int.MaxValue)]
    public int UserId { get; init; }
    [Required, RegularExpression("^(basic|pro)$")]
    public string Plan { get; init; } = "";
}

public sealed record SubscriptionResponse(int Id, int UserId, string Plan, bool IsActive);
public sealed record CreateUserRequest(
    [Required, StringLength(120, MinimumLength = 1)] string Name,
    [Required, EmailAddress, StringLength(254)] string Email);
public sealed record UserResponse(int Id, string Name, string Email);
