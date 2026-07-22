using Harness.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Harness.AuditCli;

/// <summary>
/// `harness-audit verify &lt;run-id&gt;` — recomputes a run's audit hash chain straight from
/// Postgres and the payload files, with no dependency on Harness.Api. That independence is the
/// point: a compliance colleague verifies the trail with a tool that shares nothing with the
/// service that produced it except the hash definition.
///
/// Exit codes: 0 = intact, 1 = broken, 2 = could not verify (bad usage, no database, no such run).
/// Fail-closed — anything other than a proven-intact chain is a non-zero exit.
/// </summary>
public static class Program
{
    private const int ExitIntact = 0;
    private const int ExitBroken = 1;
    private const int ExitCannotVerify = 2;

    private const string PayloadRootEnvVar = "HARNESS_AUDIT_PAYLOADS";
    private const string PasswordEnvVar = "POSTGRES_PASSWORD";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? ExitCannotVerify : ExitIntact;
        }

        if (!args[0].Equals("verify", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage();
            return ExitCannotVerify;
        }

        if (args.Length < 2 || !Guid.TryParse(args[1], out var runId))
        {
            Console.Error.WriteLine("verify needs a run id (GUID).");
            PrintUsage();
            return ExitCannotVerify;
        }

        var payloadRoot = Environment.GetEnvironmentVariable(PayloadRootEnvVar);
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--payload-root" && i + 1 < args.Length) payloadRoot = args[++i];
            else
            {
                Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                PrintUsage();
                return ExitCannotVerify;
            }
        }

        string connection;
        try
        {
            connection = BuildConnectionString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot verify: {ex.Message}");
            return ExitCannotVerify;
        }

        var options = new DbContextOptionsBuilder<HarnessDbContext>().UseNpgsql(connection).Options;
        var verifier = new ChainVerifier(new CliDbContextFactory(options), payloadRoot);

        ChainVerificationResult result;
        try
        {
            result = await verifier.VerifyAsync(runId);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // Never claim intact when the store could not be read.
            Console.Error.WriteLine($"Cannot verify run {runId}: {ex.Message}");
            return ExitCannotVerify;
        }

        Report(result, payloadRoot);

        if (result is { Intact: false, Events.Count: 0 }) return ExitCannotVerify;
        return result.Intact ? ExitIntact : ExitBroken;
    }

    /// <summary>
    /// Connection details come from the environment, mirroring the API: the base string in
    /// HARNESS_DB_CONNECTION (no password) and the password in POSTGRES_PASSWORD. The assembled
    /// string is never printed.
    /// </summary>
    private static string BuildConnectionString()
    {
        var baseConnection = Environment.GetEnvironmentVariable(HarnessDbContextFactory.ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(baseConnection))
            baseConnection = HarnessDbContextFactory.LocalDefaultConnection;

        var builder = new NpgsqlConnectionStringBuilder(baseConnection);
        var password = Environment.GetEnvironmentVariable(PasswordEnvVar);
        if (!string.IsNullOrEmpty(password)) builder.Password = password;

        if (string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException(
                $"no database password: set {PasswordEnvVar} (or include it in "
                + $"{HarnessDbContextFactory.ConnectionEnvVar}).");

        return builder.ConnectionString;
    }

    private static void Report(ChainVerificationResult result, string? payloadRoot)
    {
        // Header carries no credentials — host and database only.
        Console.WriteLine($"run      {result.RunId}");
        Console.WriteLine($"payloads {payloadRoot ?? "(as referenced by each event)"}");
        Console.WriteLine($"events   {result.Events.Count}");
        Console.WriteLine();

        if (result.Events.Count > 0)
        {
            Console.WriteLine($"{"seq",5}  {"type",-13} {"node",-12} {"ts",-21} status");
            foreach (var e in result.Events)
            {
                var status = e.Ok ? "ok" : $"BROKEN [{e.Status}] {e.Detail}";
                var ts = e.Ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                Console.WriteLine(
                    $"{e.Seq,5}  {Trim(e.Type, 13),-13} {Trim(e.Node, 12),-12} {ts,-21} {status}");
            }
            Console.WriteLine();
        }

        if (result.Intact)
        {
            Console.WriteLine($"VERDICT: intact — {result.Events.Count} event(s) verified"
                              + (result.Reason is null ? "." : $" ({result.Reason})."));
        }
        else
        {
            var where = result.FirstBrokenSeq == ChainVerificationResult.ChainLevelSeq
                ? "the chain as a whole"
                : $"seq {result.FirstBrokenSeq}";
            Console.WriteLine($"VERDICT: BROKEN at {where} — {result.Reason}");
        }
    }

    private static string Trim(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    /// <summary>No DI container here — one context per call, straight from the options.</summary>
    private sealed class CliDbContextFactory(DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            usage: harness-audit verify <run-id> [--payload-root <dir>]

            Recomputes the hash chain of a run's audit events and reports each event as ok or
            broken. Exit 0 = intact, 1 = broken, 2 = could not verify.

            environment:
              HARNESS_DB_CONNECTION   Npgsql connection string without the password
                                      (default: Host=localhost;Port=5432;Database=harness;Username=harness)
              POSTGRES_PASSWORD       database password (required)
              HARNESS_AUDIT_PAYLOADS  root of the audit payload files, if they are not mounted at
                                      the path recorded in each event (e.g. a copy of the volume)
            """);
    }
}
