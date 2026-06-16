using NanoFramework.Migrate.Core;
using Xunit;

namespace NanoFramework.Migrate.Tests;

public class CleanTests
{
    private static readonly IProjectConverter Converter = new ProjectConverter();

    private const string Nfproj = """
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup><AssemblyName>Sample</AssemblyName></PropertyGroup>
        </Project>
        """;

    [Fact]
    public void Clean_removes_all_nfproj_bak_and_nanomigrate_folders_and_reports_count()
    {
        using var dir = new TempDir();

        // A real conversion leaves a Sample.nfproj.bak behind.
        var nfproj = dir.File("a/Sample.nfproj", Nfproj);
        Converter.Convert(nfproj, new ConversionOptions());
        Assert.True(File.Exists(nfproj + ".bak"));

        // A second, nested project's backup, plus a rollback journal folder.
        var nfproj2 = dir.File("b/Other.nfproj", Nfproj);
        Converter.Convert(nfproj2, new ConversionOptions());
        var journal = RollbackJournal.Start(dir.Path);
        journal.BackupBeforeChange(dir.File("c/some.txt", "x"));
        journal.Save();
        Assert.True(Directory.Exists(Path.Combine(dir.Path, RollbackJournal.FolderName)));

        var plan = BackupCleaner.Plan(dir.Path);
        Assert.Equal(2, plan.BackupFiles.Count);
        Assert.Single(plan.RollbackFolders);
        Assert.Equal(3, plan.Total);

        var result = BackupCleaner.Remove(plan);
        Assert.Empty(result.Problems);
        Assert.Equal(3, result.Total);

        // Everything is gone; a re-plan finds nothing (idempotent).
        Assert.False(File.Exists(nfproj + ".bak"));
        Assert.False(File.Exists(nfproj2 + ".bak"));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, RollbackJournal.FolderName)));
        Assert.True(BackupCleaner.Plan(dir.Path).IsEmpty);
    }

    [Fact]
    public void Clean_on_a_tree_with_no_leftovers_is_an_empty_noop()
    {
        using var dir = new TempDir();
        dir.File("Sample.csproj", "<Project/>");

        var plan = BackupCleaner.Plan(dir.Path);
        Assert.True(plan.IsEmpty);

        var result = BackupCleaner.Remove(plan);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Clean_on_nonexistent_path_yields_empty_plan()
    {
        var plan = BackupCleaner.Plan(Path.Combine(Path.GetTempPath(), "nanomig-does-not-exist-" + Guid.NewGuid()));
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Clean_does_not_touch_unrelated_files()
    {
        using var dir = new TempDir();
        var keep = dir.File("Keep.nfproj", Nfproj);          // a live project, NOT a .bak
        var keepCs = dir.File("Sample.csproj", "<Project/>");
        dir.File("Sample.nfproj.bak", "backup");             // a leftover to remove

        var plan = BackupCleaner.Plan(dir.Path);
        Assert.Single(plan.BackupFiles);
        BackupCleaner.Remove(plan);

        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(keepCs));
        Assert.False(File.Exists(dir.Combine("Sample.nfproj.bak")));
    }
}
