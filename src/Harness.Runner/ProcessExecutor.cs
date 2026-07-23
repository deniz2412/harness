using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Harness.Runner;

/// <summary>Outcome of one raw process launch, before it is shaped into a CommandResult.</summary>
internal readonly record struct ProcessOutcome(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut);

/// <summary>
/// The single point where this project starts an OS process. Shared by the factory (for the one
/// <c>git clone</c>) and the session (for each allowlisted command) so the no-shell launch, the
/// output cap and the timeout/kill behaviour are defined once.
///
/// CONTAINER NOTE: this is the method the container implementation replaces. Instead of
/// <see cref="Process"/> against the host, it would exec inside the run's ephemeral runner container
/// (docker exec / the Jobs API). The caps, timeout and no-shell argument passing stay identical;
/// only the launch target changes. Nothing above <see cref="IRunnerSession"/> can tell which is used.
/// </summary>
internal static class ProcessExecutor
{
    internal static async Task<ProcessOutcome> ExecuteAsync(
        string program,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,          // no shell: args go straight to the child, no metachar parsing
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,     // closed immediately so a child waiting on stdin sees EOF, not a hang
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
                throw new RunnerExecutionException(
                    $"Failed to start '{program}'.", new InvalidOperationException("Process.Start returned false."));
        }
        catch (Win32Exception ex)
        {
            // Program not found / not executable. Not a refusal (it was allowlisted) and not an
            // ordinary non-zero exit (it never ran) — surface it as an execution failure.
            throw new RunnerExecutionException($"Failed to start '{program}'.", ex);
        }

        // Close stdin so children that read it terminate rather than block.
        try { process.StandardInput.Close(); } catch { /* best effort */ }

        // Start draining both pipes immediately (before WaitForExit) to avoid a full-pipe deadlock.
        // The read tasks complete naturally at EOF, which happens when the process exits or is killed.
        var stdoutTask = ReadCappedAsync(process.StandardOutput, maxOutputBytes, ct);
        var stderrTask = ReadCappedAsync(process.StandardError, maxOutputBytes, ct);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        var externalCancel = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree so no orphaned grandchild keeps running (or keeps a pipe open).
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            externalCancel = ct.IsCancellationRequested;
            timedOut = !externalCancel;
        }

        // Ensure the process has fully exited so the pipes close and the read tasks can finish.
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }

        var stdout = await SafeAwait(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeAwait(stderrTask).ConfigureAwait(false);

        if (externalCancel)
            throw new OperationCanceledException(ct);

        return new ProcessOutcome(
            ExitCode: timedOut ? -1 : process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            TimedOut: timedOut);
    }

    private static async Task<string> SafeAwait(Task<string> readTask)
    {
        try { return await readTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { return string.Empty; }
    }

    /// <summary>
    /// Reads a stream to EOF, keeping at most <paramref name="maxBytes"/> UTF-8 bytes and draining
    /// (but discarding) the rest so the child never blocks on a full pipe. If anything was discarded,
    /// the returned text ends with a <c>…[truncated N bytes]</c> marker.
    /// </summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        long keptBytes = 0;
        long discardedBytes = 0;
        var capped = false;

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            if (capped)
            {
                discardedBytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
                continue;
            }

            var chunkBytes = Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (keptBytes + chunkBytes <= maxBytes)
            {
                sb.Append(buffer, 0, read);
                keptBytes += chunkBytes;
                continue;
            }

            // This chunk crosses the cap: keep as many whole chars as fit, discard the remainder.
            var i = 0;
            while (i < read)
            {
                var cb = Encoding.UTF8.GetByteCount(buffer, i, 1);
                if (keptBytes + cb > maxBytes) break;
                sb.Append(buffer[i]);
                keptBytes += cb;
                i++;
            }
            discardedBytes += Encoding.UTF8.GetByteCount(buffer, i, read - i);
            capped = true;
        }

        if (capped)
            sb.Append($"\n…[truncated {discardedBytes} bytes]");

        return sb.ToString();
    }
}
