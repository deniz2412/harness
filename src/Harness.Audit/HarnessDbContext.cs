using Harness.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Harness.Audit;

public sealed class HarnessDbContext(DbContextOptions<HarnessDbContext> options) : DbContext(options)
{
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunEvent> Events => Set<RunEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Run>().HasKey(r => r.Id);
        b.Entity<RunEvent>().HasKey(e => e.EventId);
        b.Entity<RunEvent>().HasIndex(e => new { e.RunId, e.Seq }).IsUnique();
    }
}
