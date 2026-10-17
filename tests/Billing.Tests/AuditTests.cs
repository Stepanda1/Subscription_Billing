using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Subscription_Backend;
using Subscription_Backend.Dtos;
using Subscription_Backend.Messages;
using Subscription_Backend.Models;
using Subscription_Backend.Services;
using Xunit;

namespace Billing.Tests;

public sealed partial class BillingTests
{
    [Fact]
    public async Task Cancellation_after_payment_insert_rolls_back_and_leaves_key_reusable()
    {
        var user = await User();
        var key = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        fixture.Fault.CancelAfterPayment = cancellation;
        try
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateSubscriptionAsync(
                new CreateSubscriptionRequest { UserId = user.Id, Plan = "basic" }, key, cancellation.Token));
        }
        finally { fixture.Fault.CancelAfterPayment = null; }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, await fixture.WithDb(db => db.Subscriptions.CountAsync(x => x.UserId == user.Id)));
        Assert.Equal(0, await fixture.WithDb(db => db.Payments.CountAsync(x => x.Subscription.UserId == user.Id)));
        Assert.False(await fixture.WithDb(db => db.IdempotencyRecords.AnyAsync(x => x.Key == key)));
        Assert.Equal(HttpStatusCode.Accepted, (await Create(user.Id, key)).StatusCode);
    }

    [Fact]
    public async Task Lost_commit_acknowledgement_replays_the_committed_response_without_second_charge()
    {
        var user = await User();
        var key = Guid.NewGuid();
        fixture.CommitFault.Enabled = true;
        try { Assert.Equal(HttpStatusCode.InternalServerError, (await Create(user.Id, key)).StatusCode); }
        finally { fixture.CommitFault.Enabled = false; }
        var id = await fixture.WithDb(db => db.Subscriptions.Where(x => x.UserId == user.Id).Select(x => x.Id).SingleAsync());
        var response = await Create(user.Id, key);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var replay = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>())!;
        Assert.Equal(id, replay.Id);
        Assert.False(replay.IsActive);
        Assert.Equal(1, await fixture.WithDb(db => db.Payments.CountAsync(x => x.Subscription.UserId == user.Id)));
        await WaitActive(id);
    }

    [Fact]
    public async Task Concurrent_conflicting_payloads_have_one_winner_and_one_charge()
    {
        var user = await User();
        var key = Guid.NewGuid();
        var requests = Enumerable.Range(0, 20).Select(i => new { Plan = i % 2 == 0 ? "basic" : "pro" }).ToArray();
        var responses = await Task.WhenAll(requests.Select(x => Create(user.Id, key, x.Plan)));
        var winner = requests[Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.Accepted)].Plan;
        for (var i = 0; i < responses.Length; i++)
            Assert.Equal(requests[i].Plan == winner ? HttpStatusCode.Accepted : HttpStatusCode.Conflict, responses[i].StatusCode);
        var acceptedBodies = await Task.WhenAll(responses.Where(x => x.StatusCode == HttpStatusCode.Accepted)
            .Select(x => x.Content.ReadAsStringAsync()));
        Assert.Single(acceptedBodies.Distinct());
        var amount = await fixture.WithDb(db => db.Payments.Where(x => x.Subscription.UserId == user.Id).Select(x => x.Amount).SingleAsync());
        Assert.Equal(PlanCatalog.Price(winner), amount);
    }

    [Fact]
    public async Task Cancelling_a_request_waiting_for_the_key_releases_its_transaction()
    {
        var user = await User();
        var key = Guid.NewGuid();
        var lockId = BitConverter.ToInt64(SHA256.HashData(key.ToByteArray()), 0);
        await using var ownerScope = fixture.Services.CreateAsyncScope();
        var owner = ownerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await owner.Database.BeginTransactionAsync();
        await owner.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var requestScope = fixture.Services.CreateAsyncScope();
        var service = requestScope.ServiceProvider.GetRequiredService<SubscriptionService>();
        var pending = service.CreateSubscriptionAsync(new CreateSubscriptionRequest { UserId = user.Id, Plan = "basic" }, key, cancellation.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var waiting = 0;
            while (DateTime.UtcNow < deadline && waiting == 0)
            {
                waiting = await owner.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted").SingleAsync();
                if (waiting == 0) await Task.Delay(25);
            }
            Assert.True(waiting > 0, "The second transaction never waited for the advisory lock.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
        }
        Assert.False(await fixture.WithDb(db => db.IdempotencyRecords.AnyAsync(x => x.Key == key)));
        Assert.Equal(HttpStatusCode.Accepted, (await Create(user.Id, key)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_business_and_transport_duplicates_are_safe()
    {
        var user = await User();
        var seed = await fixture.WithDb(async db =>
        {
            var subscription = new Subscription { UserId = user.Id, Plan = "basic" };
            db.Payments.Add(new Payment { Subscription = subscription, Amount = 990m });
            await db.SaveChangesAsync();
            return subscription.Id;
        });
        var bus = fixture.Services.GetRequiredService<IBus>();
        var message = new SubscriptionPaidEvent { SubscriptionId = seed, Amount = 990m, PaidAt = DateTime.UtcNow };
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(ids.SelectMany(id => Enumerable.Range(0, 2).Select(_ =>
            bus.Publish(message, ctx => ctx.MessageId = id))));
        await WaitActive(seed);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && await fixture.WithDb(db => db.Set<InboxState>()
                   .CountAsync(x => ids.Contains(x.MessageId) && x.Consumed != null)) < ids.Length)
            await Task.Delay(50);
        Assert.Equal(ids.Length, await fixture.WithDb(db => db.Set<InboxState>()
            .CountAsync(x => ids.Contains(x.MessageId) && x.Consumed != null)));
        Assert.Equal(1, await fixture.WithDb(db => db.Payments.CountAsync(x => x.SubscriptionId == seed)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_payment_event_is_quarantined_without_activation(bool missingPayment)
    {
        var user = await User();
        var id = await SeedPending(user.Id);
        var messageId = Guid.NewGuid();
        await fixture.Services.GetRequiredService<IBus>().Publish(new SubscriptionPaidEvent
        {
            SubscriptionId = missingPayment ? int.MaxValue : id,
            Amount = missingPayment ? 990m : 1m,
            PaidAt = DateTime.UtcNow
        }, ctx => ctx.MessageId = messageId);
        var error = await fixture.WaitForError(messageId);
        Assert.Equal("fault", error.GetProperty("properties").GetProperty("headers").GetProperty("MT-Reason").GetString());
        Assert.False(await fixture.WithDb(db => db.Subscriptions.Where(x => x.Id == id).Select(x => x.IsActive).SingleAsync()));
        Assert.False(await fixture.WithDb(db => db.Set<InboxState>().AnyAsync(x => x.MessageId == messageId && x.Consumed != null)));
        Assert.Equal(1, await fixture.WithDb(db => db.Payments.CountAsync(x => x.SubscriptionId == id)));
        // A poison message must not prevent the following legitimate event.
        await fixture.Services.GetRequiredService<IBus>().Publish(new SubscriptionPaidEvent
            { SubscriptionId = id, Amount = 990m, PaidAt = DateTime.UtcNow });
        await WaitActive(id);
    }

    [Fact]
    public async Task Failed_activation_update_rolls_back_before_retry_and_commits_with_inbox()
    {
        var user = await User();
        var id = await SeedPending(user.Id);
        var messageId = Guid.NewGuid();
        fixture.ActivationFault.SubscriptionId = id;
        try
        {
            await fixture.Services.GetRequiredService<IBus>().Publish(new SubscriptionPaidEvent
                { SubscriptionId = id, Amount = 990m, PaidAt = DateTime.UtcNow }, ctx => ctx.MessageId = messageId);
            await fixture.ActivationFault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // The UPDATE ran on the consumer connection, but has not committed.
            Assert.False(await fixture.WithDb(db => db.Subscriptions.Where(x => x.Id == id).Select(x => x.IsActive).SingleAsync()));
            Assert.False(await fixture.WithDb(db => db.Set<InboxState>().AnyAsync(x => x.MessageId == messageId && x.Consumed != null)));
        }
        finally { fixture.ActivationFault.Release.TrySetResult(); }
        await WaitActive(id);
        Assert.Equal(1, fixture.ActivationFault.Failures);
        Assert.True(await fixture.WithDb(db => db.Set<InboxState>().AnyAsync(x => x.MessageId == messageId && x.Consumed != null)));
    }

    private Task<int> SeedPending(int userId) => fixture.WithDb(async db =>
    {
        var subscription = new Subscription { UserId = userId, Plan = "basic" };
        db.Payments.Add(new Payment { Subscription = subscription, Amount = 990m });
        await db.SaveChangesAsync();
        return subscription.Id;
    });
}
