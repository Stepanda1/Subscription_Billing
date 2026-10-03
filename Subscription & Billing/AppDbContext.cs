using MassTransit;
using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Models;

namespace Subscription_Backend;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>().Property(x => x.Name).HasMaxLength(120);
        model.Entity<User>().Property(x => x.Email).HasMaxLength(254);
        model.Entity<User>().HasIndex(x => x.Email).IsUnique();
        model.Entity<Subscription>().Property(x => x.Plan).HasMaxLength(32);
        model.Entity<Subscription>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        model.Entity<Subscription>().HasIndex(x => new { x.UserId, x.Id });
        model.Entity<Payment>().HasIndex(x => x.SubscriptionId).IsUnique();
        model.Entity<Payment>().Property(x => x.Amount).HasPrecision(18, 2);
        model.Entity<Payment>().ToTable(t => t.HasCheckConstraint("CK_Payment_Amount", "\"Amount\" > 0"));
        model.Entity<IdempotencyRecord>().HasKey(x => x.Key);
        model.Entity<IdempotencyRecord>().Property(x => x.RequestHash).HasMaxLength(64);
        model.AddInboxStateEntity();
        model.AddOutboxMessageEntity();
        model.AddOutboxStateEntity();
    }
}
