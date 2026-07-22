using Harness.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Harness.Audit.Tests;

/// <summary>
/// One in-memory database plus one temp payload directory per test, wired into a real
/// <see cref="AuditEmitter"/> and <see cref="ChainVerifier"/>. Nothing here touches Postgres,
/// the network or docker — the audit layer has to be testable on a laptop with no stack running.
/// </summary>
internal sealed class AuditTestHarness : IDbContextFactory<HarnessDbContext>, IDisposable
{
    // One root for the whole test assembly, a unique database name per harness: that keeps tests
    // isolated while letting EF reuse a single internal service provider (a root per test trips
    // ManyServiceProvidersCreatedWarning once the suite passes twenty tests).
    private static readonly InMemoryDatabaseRoot Root = new();

    private readonly DbContextOptions<HarnessDbContext> _options;

    public string PayloadDir { get; }
    public AuditEmitter Emitter { get; }
    public ChainVerifier Verifier { get; }

    public AuditTestHarness()
    {
        _options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), Root)
            .Options;
        PayloadDir = Path.Combine(Path.GetTempPath(), "harness-audit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(PayloadDir);
        Emitter = new AuditEmitter(this, PayloadDir);
        Verifier = new ChainVerifier(this);
    }

    public HarnessDbContext CreateDbContext() => new(_options);

    /// <summary>Convenience so tests can await a context without casting to the interface.</summary>
    public Task<HarnessDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
        Task.FromResult(CreateDbContext());

    /// <summary>The payload file for a given event, as the emitter names them.</summary>
    public string PayloadFile(Guid runId, long seq) =>
        Path.Combine(PayloadDir, runId.ToString(), $"{seq:D6}.json");

    public void Dispose()
    {
        try { Directory.Delete(PayloadDir, recursive: true); }
        catch (IOException) { /* a leftover temp dir is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }
}
