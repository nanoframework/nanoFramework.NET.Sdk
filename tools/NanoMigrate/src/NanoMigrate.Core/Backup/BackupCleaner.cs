namespace NanoFramework.Migrate.Core.Backup;

/// <summary>The leftovers a clean would remove, gathered for preview before deletion.</summary>
public sealed class CleanPlan
{
    /// <summary>The root the scan was rooted at (absolute).</summary>
    public required string Root { get; init; }

    /// <summary><c>*.nfproj.bak</c> files found under the root.</summary>
    public List<string> BackupFiles { get; } = new();

    /// <summary><c>.nanomigrate</c> rollback folders found under the root.</summary>
    public List<string> RollbackFolders { get; } = new();

    /// <summary>Total leftovers (backup files + rollback folders).</summary>
    public int Total => BackupFiles.Count + RollbackFolders.Count;

    /// <summary>True when there is nothing to remove.</summary>
    public bool IsEmpty => Total == 0;
}

/// <summary>The tally of an executed clean.</summary>
public sealed class CleanResult
{
    /// <summary><c>*.nfproj.bak</c> files actually deleted.</summary>
    public List<string> RemovedBackups { get; } = new();

    /// <summary><c>.nanomigrate</c> folders actually removed.</summary>
    public List<string> RemovedFolders { get; } = new();

    /// <summary>Non-fatal problems (a file that could not be deleted, etc.).</summary>
    public List<string> Problems { get; } = new();

    /// <summary>Total leftovers removed.</summary>
    public int Total => RemovedBackups.Count + RemovedFolders.Count;
}

/// <summary>
/// Finds and removes migration leftovers under a path: every <c>*.nfproj.bak</c>
/// file and every <c>.nanomigrate</c> rollback folder/journal. Pure file logic —
/// the planning (<see cref="Plan"/>) and the removal (<see cref="Remove"/>) are
/// separate so a command can preview, confirm, then act. Idempotent and safe: a
/// tree with no leftovers yields an empty plan and a no-op clean.
/// </summary>
public static class BackupCleaner
{
    /// <summary>
    /// Scans <paramref name="root"/> (recursively) for migration leftovers. A
    /// non-existent root yields an empty plan. <c>.nanomigrate</c> folders are
    /// reported as folders (their contents are not enumerated as individual files).
    /// </summary>
    public static CleanPlan Plan(string root)
    {
        var full = Path.GetFullPath(root);
        var plan = new CleanPlan { Root = full };
        if (!Directory.Exists(full) && !File.Exists(full)) return plan;

        // A single file argument: only its sibling .bak makes sense; treat its dir as root.
        var scanRoot = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;

        foreach (var bak in SafeEnumerateFiles(scanRoot, "*.nfproj.bak"))
            plan.BackupFiles.Add(Path.GetFullPath(bak));

        foreach (var dir in SafeEnumerateDirectories(scanRoot, RollbackJournal.FolderName))
            plan.RollbackFolders.Add(Path.GetFullPath(dir));

        plan.BackupFiles.Sort(StringComparer.OrdinalIgnoreCase);
        plan.RollbackFolders.Sort(StringComparer.OrdinalIgnoreCase);
        return plan;
    }

    /// <summary>
    /// Removes everything in <paramref name="plan"/>. Returns a tally; per-item
    /// failures are collected as problems rather than aborting the sweep.
    /// </summary>
    public static CleanResult Remove(CleanPlan plan)
    {
        var result = new CleanResult();

        foreach (var bak in plan.BackupFiles)
        {
            try
            {
                if (File.Exists(bak)) File.Delete(bak);
                result.RemovedBackups.Add(bak);
            }
            catch (Exception ex) { result.Problems.Add($"could not delete {bak}: {ex.Message}"); }
        }

        foreach (var dir in plan.RollbackFolders)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                result.RemovedFolders.Add(dir);
            }
            catch (Exception ex) { result.Problems.Add($"could not remove {dir}: {ex.Message}"); }
        }

        return result;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string name)
    {
        try
        {
            return Directory.EnumerateDirectories(root, name, SearchOption.AllDirectories)
                .Where(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
