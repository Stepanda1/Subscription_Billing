using Microsoft.EntityFrameworkCore;
using Subscription_Backend.Models;
using System.Collections.Generic;

namespace Subscription_Backend
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users => Set<User>();
        public DbSet<Subscription> Subscriptions => Set<Subscription>();
        public DbSet<Payment> Payments => Set<Payment>();
        public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    }
}
