using System.Text;

namespace Harness.Tools;

/// <summary>Read-only repo access plus, in M2, write_worktree — a write bounded to the run's worktree.</summary>
public sealed class RepoToolset(string worktreeRoot)
{
    private string Resolve(string relative) => ResolveWithin(worktreeRoot, relative);

    /// <summary>
    /// Resolve <paramref name="relative"/> against <paramref name="root"/>, refusing anything that
    /// escapes it. The separator matters (the M1 fix): a bare prefix test also accepts a sibling
    /// directory whose name merely starts with the root — "/data/worktrees" would admit
    /// "/data/worktrees-evil/x". The write path supplies the run's per-run worktree as
    /// <paramref name="root"/>, so the same one check guards reads and writes.
    /// </summary>
    private static string ResolveWithin(string root, string relative)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (full != rootFull && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path escapes the worktree.");
        return full;
    }

    public Task<string> ReadFile(string path) => File.ReadAllTextAsync(Resolve(path));

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/> inside <paramref name="worktreePath"/>,
    /// creating parent directories as needed. The worktree root is the run's isolated sandbox, injected
    /// by the ToolRegistry closure (from <c>ctx.Runner!.WorktreePath</c>) — the agent never names it — so
    /// a write with no isolated worktree cannot happen (invariant 2). Path traversal is rejected by the
    /// same separator-safe check reads use; the write stays inside the worktree.
    /// </summary>
    public async Task<string> WriteFile(string worktreePath, string path, string content)
    {
        var full = ResolveWithin(worktreePath, path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(full, content);
        return $"Wrote {Encoding.UTF8.GetByteCount(content)} bytes to {path}";
    }

    public Task<string> ListFiles(string dir = ".") =>
        Task.FromResult(string.Join("\n",
            Directory.EnumerateFiles(Resolve(dir), "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                .Select(p => Path.GetRelativePath(worktreeRoot, p))));

    /// <summary>Naive code search for M0 — replace with ripgrep if it proves insufficient.</summary>
    public Task<string> Search(string term) =>
        Task.FromResult(string.Join("\n",
            Directory.EnumerateFiles(worktreeRoot, "*", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                .SelectMany(p => File.ReadLines(p)
                    .Select((line, i) => (line, i))
                    .Where(t => t.line.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Select(t => $"{Path.GetRelativePath(worktreeRoot, p)}:{t.i + 1}: {t.line.Trim()}"))
                .Take(200)));
}
