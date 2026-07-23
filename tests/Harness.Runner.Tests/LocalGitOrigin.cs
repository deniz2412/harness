using System.Diagnostics;
using System.Text;

namespace Harness.Runner.Tests;

/// <summary>
/// A throwaway local git repository built entirely on disk — a bare "origin" plus one seeded commit,
/// created by shelling out to the real <c>git</c>. This is what lets the whole suite run offline:
/// the factory clones from this local path instead of github.com (no network, no real GitHub, no
/// docker). Implements <see cref="IDisposable"/> to delete its temp tree.
/// </summary>
internal sealed class LocalGitOrigin : IDisposable
{
    /// <summary>Filesystem path to the bare repo — feed this to the factory as the clone source.</summary>
    public string BarePath { get; }

    public const string SeededFile = "README.md";
    public const string SeededContent = "harness runner offline test fixture\n";

    private readonly string _root;

    private LocalGitOrigin(string root, string barePath)
    {
        _root = root;
        BarePath = barePath;
    }

    public static LocalGitOrigin Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var bare = Path.Combine(root, "origin.git");
        var work = Path.Combine(root, "work");

        Git(root, "init", "--bare", bare);
        Git(root, "clone", bare, work);

        File.WriteAllText(Path.Combine(work, SeededFile), SeededContent);
        Git(work, "add", SeededFile);
        Git(work, "-c", "user.email=test@harness.local", "-c", "user.name=Harness Test",
            "commit", "-m", "seed");
        // Push the working clone's current branch and set origin's HEAD to it, so a default-branch
        // (HEAD-sentinel) clone resolves regardless of whether git's default is master or main.
        Git(work, "push", "origin", "HEAD");

        return new LocalGitOrigin(root, bare);
    }

    private static void Git(string workdir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workdir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {p.ExitCode}):\n{stderr}\n{stdout}");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }
}
