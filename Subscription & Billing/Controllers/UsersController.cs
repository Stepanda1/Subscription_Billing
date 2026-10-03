using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Subscription_Backend.Dtos;
using Subscription_Backend.Models;

namespace Subscription_Backend.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UsersController(AppDbContext db) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem(statusCode: 400, detail: "Name must not be blank.");
        var user = new User { Name = request.Name.Trim(), Email = request.Email.Trim().ToLowerInvariant() };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { return Problem(statusCode: 409, detail: "Email is already registered."); }
        return CreatedAtAction(nameof(Get), new { id = user.Id }, new UserResponse(user.Id, user.Name, user.Email));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Get(int id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new UserResponse(x.Id, x.Name, x.Email)).SingleOrDefaultAsync(ct);
        return user is null ? NotFound() : Ok(user);
    }
}
