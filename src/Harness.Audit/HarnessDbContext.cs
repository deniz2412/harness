using Harness.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Harness.Audit;

public sealed class HarnessDbContext(DbContextOptions<HarnessDbContext> options) : DbContext(options)
{
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunEvent> Events => Set<RunEvent>();
    public DbSet<GateApproval> Approvals => Set<GateApproval>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Run>().HasKey(r => r.Id);

        b.Entity<RunEvent>(e =>
        {
            e.HasKey(x => x.EventId);
            // Unique, not merely indexed: the hash chain's integrity story rests on seq being
            // unique per run, and this is what serves /runs/{id}/events and every verification.
            e.HasIndex(x => new { x.RunId, x.Seq }).IsUnique();
        });

        b.Entity<GateApproval>(a =>
        {
            a.HasKey(x => x.Id);
            // One gate per (run, node) — a second row would let a rejected gate be re-requested
            // into an approved one. Also the lookup the approve/reject path uses.
            a.HasIndex(x => new { x.RunId, x.Node }).IsUnique();
            // Concurrency token: the UPDATE carries `WHERE decision = <the value we read>`, so two
            // simultaneous deciders cannot both win. Fail-closed at the database, not just in C#.
            a.Property(x => x.Decision).IsConcurrencyToken();
        });
    }
}
