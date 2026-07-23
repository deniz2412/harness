namespace Harness.Runner;

/// <summary>Robust, best-effort recursive delete of a worktree directory.</summary>
internal static class WorktreeCleanup
{
    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it, clearing read-only attributes first —
    /// git packs itself as read-only on Windows, which otherwise defeats <see cref="Directory.Delete(string,bool)"/>.
    /// Never throws: a leftover temp directory is a leak, not a failure.
    /// </summary>
    internal static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        try
        {
            ClearReadOnly(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort — swallow. The worktree root lives under a temp/volume path that is
            // periodically reclaimed anyway.
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        dir.Attributes = FileAttributes.Normal;
        foreach (var file in dir.GetFiles())
            file.Attributes = FileAttributes.Normal;
        foreach (var sub in dir.GetDirectories())
            ClearReadOnly(sub);
    }
}
