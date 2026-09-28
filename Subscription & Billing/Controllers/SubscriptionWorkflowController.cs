using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Dtos;
using Subscription_Backend.Services;

namespace Subscription_Backend.Controllers;

[ApiController]
[Route("api/subscriptions")]
public sealed class SubscriptionWorkflowController(SubscriptionService service, AppDbContext db) : ControllerBase
{
    [HttpPost("create")]
    [ProducesResponseType<SubscriptionResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SubscriptionResponse>> Create(CreateSubscriptionRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid key, CancellationToken ct)
    {
        if (key == Guid.Empty)
            return Problem(statusCode: 400, detail: "A non-empty UUID Idempotency-Key header is required.");
        var result = await service.CreateSubscriptionAsync(request, key, ct);
        return AcceptedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<SubscriptionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubscriptionResponse>> Get(int id, CancellationToken ct)
    {
        var result = await db.Subscriptions.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new SubscriptionResponse(x.Id, x.UserId, x.Plan, x.IsActive)).SingleOrDefaultAsync(ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SubscriptionResponse>>> List(
        [FromQuery] int userId, [FromQuery] int afterId = 0, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        if (userId < 1 || afterId < 0 || limit is < 1 or > 100)
            return Problem(statusCode: 400, detail: "userId > 0, afterId >= 0, limit between 1 and 100 required.");
        return Ok(await db.Subscriptions.AsNoTracking().Where(x => x.UserId == userId && x.Id > afterId)
            .OrderBy(x => x.Id).Take(limit)
            .Select(x => new SubscriptionResponse(x.Id, x.UserId, x.Plan, x.IsActive)).ToListAsync(ct));
    }
}
