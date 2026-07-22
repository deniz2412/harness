namespace Harness.Tools;

/// <summary>Read-only repo access for M0; write_worktree arrives with runner isolation in M2.</summary>
public sealed class RepoToolset(string worktreeRoot)
{
    private string Resolve(string relative)
    {
        // The separator matters: a bare prefix test also accepts a sibling directory whose name
        // merely starts with the root — "/data/worktrees" would admit "/data/worktrees-evil/x".
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path escapes the worktree.");
        return full;
    }

    public Task<string> ReadFile(string path) => File.ReadAllTextAsync(Resolve(path));

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
