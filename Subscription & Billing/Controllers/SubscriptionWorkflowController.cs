using Microsoft.AspNetCore.Mvc;
using Subscription_Backend.Dtos;
using Subscription_Backend.Models;
using Subscription_Backend.Services;

namespace Subscription_Backend.Controllers;

[ApiController]
[Route("api/subscriptions")]
public class SubscriptionWorkflowController : ControllerBase
{
    private readonly SubscriptionService _service;

    public SubscriptionWorkflowController(SubscriptionService service)
    {
        _service = service;
    }

    [HttpPost("create")]
    public async Task<ActionResult<Subscription>> Create(
    CreateSubscriptionRequest request,
    [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey)
    {
        if (idempotencyKey == Guid.Empty)
            return BadRequest("Idempotency-Key header is required");

        var subscription = await _service.CreateSubscriptionAsync(
            request,
            idempotencyKey);

        return Ok(subscription);
    }

}
