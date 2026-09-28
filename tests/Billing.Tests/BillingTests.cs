using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Subscription_Backend.Dtos;
using Subscription_Backend.Messages;
using Subscription_Backend.Services;
using Xunit;

namespace Billing.Tests;

public sealed class BillingTests(BillingFixture fixture) : IClassFixture<BillingFixture>
{
    private HttpClient Client => fixture.Client;

    private async Task<UserResponse> User()
    {
        var response = await Client.PostAsJsonAsync("/api/users", new { name = "Test", email = $"{Guid.NewGuid():N}@example.com" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserResponse>())!;
    }

    private Task<HttpResponseMessage> Create(int userId, Guid key, string plan = "basic")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/create")
        { Content = JsonContent.Create(new { userId, plan }) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return Client.SendAsync(request);
    }

    private async Task WaitActive(int id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var result = await Client.GetFromJsonAsync<SubscriptionResponse>($"/api/subscriptions/{id}");
            if (result!.IsActive) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Subscription {id} was not activated.");
    }

    [Fact]
    public async Task Payment_is_persisted_and_activated_through_real_broker()
    {
        var user = await User();
        var response = await Create(user.Id, Guid.NewGuid(), "pro");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var subscription = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>())!;
        Assert.NotNull(response.Headers.Location);
        Assert.False(subscription.IsActive);
        await WaitActive(subscription.Id);
        var amount = await fixture.WithDb(db => db.Payments.Where(x => x.SubscriptionId == subscription.Id).Select(x => x.Amount).SingleAsync());
        Assert.Equal(1990m, amount);
    }

    [Fact]
    public async Task Twenty_parallel_retries_create_one_payment_and_replay_identical_response()
    {
        var user = await User();
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Create(user.Id, key)));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        var count = await fixture.WithDb(db => db.Payments.CountAsync(x => x.Subscription.UserId == user.Id));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Key_reuse_with_another_payload_returns_conflict()
    {
        var user = await User();
        var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await Create(user.Id, key)).StatusCode);
        var response = await Create(user.Id, key, "pro");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1, await fixture.WithDb(db => db.Payments.CountAsync(x => x.Subscription.UserId == user.Id)));
    }

    [Fact]
    public async Task Saturated_api_returns_429_but_liveness_still_works()
    {
        var user = await User();
        var key = Guid.NewGuid();
        var lockId = BitConverter.ToInt64(SHA256.HashData(key.ToByteArray()), 0);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Subscription_Backend.AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})");
        var pending = Enumerable.Range(0, 24).Select(_ => Create(user.Id, key)).ToArray();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var waiting = 0;
            while (DateTime.UtcNow < deadline && waiting < 24)
            {
                waiting = await db.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted").SingleAsync();
                if (waiting < 24) await Task.Delay(50);
            }
            Assert.Equal(24, waiting);
            var rejected = await Client.GetAsync($"/api/subscriptions?userId={user.Id}");
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType!.MediaType);
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health/live")).StatusCode);
        }
        finally { await tx.RollbackAsync(); }
        Assert.All(await Task.WhenAll(pending), response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(1, await fixture.WithDb(context => context.Payments.CountAsync(x => x.Subscription.UserId == user.Id)));
    }

    [Fact]
    public async Task Missing_user_rolls_back_without_reserving_key()
    {
        var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await Create(int.MaxValue, key)).StatusCode);
        Assert.False(await fixture.WithDb(db => db.IdempotencyRecords.AnyAsync(x => x.Key == key)));
        var user = await User();
        Assert.Equal(HttpStatusCode.Accepted, (await Create(user.Id, key)).StatusCode);
    }

    [Fact]
    public async Task Failure_after_payment_insert_rolls_back_payment_subscription_and_outbox()
    {
        var user = await User();
        var key = Guid.NewGuid();
        fixture.Fault.Enabled = true;
        try { Assert.Equal(HttpStatusCode.InternalServerError, (await Create(user.Id, key)).StatusCode); }
        finally { fixture.Fault.Enabled = false; }
        Assert.NotEqual(Guid.Empty, fixture.Fault.FailedMessageId);
        Assert.False(await fixture.WithDb(db => db.Set<OutboxMessage>().AnyAsync(x => x.MessageId == fixture.Fault.FailedMessageId)));
        Assert.Equal(0, await fixture.WithDb(db => db.Subscriptions.CountAsync(x => x.UserId == user.Id)));
        Assert.Equal(0, await fixture.WithDb(db => db.Payments.CountAsync(x => x.Subscription.UserId == user.Id)));
        Assert.False(await fixture.WithDb(db => db.IdempotencyRecords.AnyAsync(x => x.Key == key)));
        // The same key must remain usable after rollback.
        Assert.Equal(HttpStatusCode.Accepted, (await Create(user.Id, key)).StatusCode);
    }

    [Theory]
    [InlineData(0, "basic")]
    [InlineData(1, "free")]
    [InlineData(1, "")]
    public async Task Invalid_input_returns_400(int userId, string plan)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(userId, Guid.NewGuid(), plan)).StatusCode);
    }

    [Fact]
    public async Task Missing_idempotency_header_returns_400()
    {
        var response = await Client.PostAsJsonAsync("/api/subscriptions/create", new { userId = 1, plan = "basic" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Email_is_unique_case_insensitively()
    {
        var user = await User();
        var response = await Client.PostAsJsonAsync("/api/users", new { name = "Other", email = user.Email.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Client_cannot_override_charge_amount()
    {
        var user = await User();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/create")
        { Content = JsonContent.Create(new { userId = user.Id, plan = "pro", amount = 1 }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.SendAsync(request)).StatusCode);
        Assert.Equal(0, await fixture.WithDb(db => db.Payments.CountAsync(x => x.Subscription.UserId == user.Id)));
    }

    [Fact]
    public async Task Pagination_is_bounded_and_has_no_duplicate_rows()
    {
        var user = await User();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Accepted, (await Create(user.Id, Guid.NewGuid())).StatusCode);
        var first = (await Client.GetFromJsonAsync<SubscriptionResponse[]>($"/api/subscriptions?userId={user.Id}&limit=2"))!;
        var second = (await Client.GetFromJsonAsync<SubscriptionResponse[]>($"/api/subscriptions?userId={user.Id}&limit=2&afterId={first[^1].Id}"))!;
        Assert.Equal(2, first.Length);
        Assert.Single(second);
        Assert.True(second[0].Id > first[^1].Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"/api/subscriptions?userId={user.Id}&limit=101")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_business_event_with_different_message_id_does_not_duplicate_payment()
    {
        var user = await User();
        var result = (await (await Create(user.Id, Guid.NewGuid())).Content.ReadFromJsonAsync<SubscriptionResponse>())!;
        await WaitActive(result.Id);
        var messageId = Guid.NewGuid();
        // Publish directly, deliberately bypassing request-scoped bus outbox.
        await fixture.Services.GetRequiredService<IBus>().Publish(new SubscriptionPaidEvent
        { SubscriptionId = result.Id, Amount = 990m, PaidAt = DateTime.UtcNow }, ctx => ctx.MessageId = messageId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !await fixture.WithDb(db => db.Set<InboxState>().AnyAsync(x => x.MessageId == messageId && x.Consumed != null)))
            await Task.Delay(100);
        Assert.True(await fixture.WithDb(db => db.Set<InboxState>().AnyAsync(x => x.MessageId == messageId && x.Consumed != null)));
        Assert.Equal(1, await fixture.WithDb(db => db.Payments.CountAsync(x => x.SubscriptionId == result.Id)));
        await WaitActive(result.Id);
    }

    [Theory]
    [InlineData("basic", 990)]
    [InlineData("pro", 1990)]
    public void Catalog_owns_charge_amount(string plan, int price) => Assert.Equal(price, PlanCatalog.Price(plan));

    [Fact]
    public void Catalog_rejects_unknown_plan() => Assert.Throws<BillingException>(() => PlanCatalog.Price("free"));
}
