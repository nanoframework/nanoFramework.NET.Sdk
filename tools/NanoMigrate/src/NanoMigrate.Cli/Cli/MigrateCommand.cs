using System.ComponentModel;
using NanoFramework.Migrate.Core;
using NanoFramework.Migrate.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;
using static NanoFramework.Migrate.Cli.Rendering.ConsoleSupport;

namespace NanoFramework.Migrate.Cli.Commands;

internal sealed class MigrateSettings : CommandSettings
{
    [CommandArgument(0, "<path>")]
    [Description("A .nfproj file, or a directory under which every .nfproj is converted.")]
    public string Path { get; init; } = "";

    [CommandOption("--sdk <version>")]
    [Description("Accepted for back-compat but ignored: the SDK reference is versionless (pinned via global.json msbuild-sdks).")]
    public string Sdk { get; init; } = "2.0.0";

    [CommandOption("--tfm <moniker>")]
    [Description("Target framework moniker (default netnano1.0).")]
    public string Tfm { get; init; } = "netnano1.0";

    [CommandOption("--ext <ext>")]
    [Description("Output extension: .nfproj or .csproj (default .csproj).")]
    public string Ext { get; init; } = ".csproj";

    [CommandOption("--no-backup")]
    [Description("Don't write a .nfproj.bak.")]
    public bool NoBackup { get; init; }

    [CommandOption("--dry-run|--no-write")]
    [Description("Analyse and preview only; write nothing.")]
    public bool DryRun { get; init; }

    [CommandOption("--glob <pattern>")]
    [Description("Only convert .nfproj whose path (relative to <path>) matches the glob. Supports *, ** and ?. Example: \"Beginner/**\".")]
    public string? Glob { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Skip the interactive \"Proceed?\" confirmation on a real run. (Non-interactive runs never prompt regardless.)")]
    public bool AssumeYes { get; init; }

    public override ValidationResult Validate()
    {
        if (Ext is not (".nfproj" or ".csproj"))
            return ValidationResult.Error("--ext must be .nfproj or .csproj");
        return ValidationResult.Success();
    }

    public ConversionOptions ToConversionOptions() => new()
    {
        Sdk = Sdk,
        Tfm = Tfm,
        Ext = Ext,
        NoBackup = NoBackup,
        DryRun = DryRun,
        Glob = Glob,
    };
}

internal sealed class MigrateCommand : Command<MigrateSettings>
{
    private readonly IProjectConverter _converter = new ProjectConverter();

    protected override int Execute(CommandContext context, MigrateSettings settings, CancellationToken cancellationToken)
    {
        Header("NanoMigrate");

        var o = settings.ToConversionOptions();
        var path = settings.Path;
        var targets = ProjectScanner.ResolveProjects(path, o.Glob);

        // Reentrant: a fully-converted tree has no .nfproj left. Exit cleanly (0)
        // rather than erroring, so re-running the converter over a repo is a safe
        // no-op.
        if (targets.Count == 0)
        {
            var why = o.Glob is null
                ? $"no .nfproj found under '{Esc(path)}' (already SDK-style?)."
                : $"no .nfproj matched glob '{Esc(o.Glob)}' under '{Esc(path)}'.";
            AnsiConsole.MarkupLine($"[grey]nothing to convert: {why}[/]");
            return 0;
        }

        // Determine the base directory glob/relative paths are reported against.
        var baseDir = Directory.Exists(path) ? Path.GetFullPath(path)
                                             : Path.GetDirectoryName(Path.GetFullPath(path))!;

        AnsiConsole.MarkupLine(o.DryRun
            ? $"[yellow]Dry run[/] — analysing [bold]{targets.Count}[/] project(s) under [blue]{Esc(baseDir)}[/]. Nothing will be written."
            : $"Found [bold]{targets.Count}[/] project(s) to convert under [blue]{Esc(baseDir)}[/].");
        AnsiConsole.WriteLine();

        // Real, interactive runs confirm once before touching disk. In dry-run or
        // when stdin is redirected (CI/automation) we proceed without prompting so
        // nothing blocks. --yes also skips the prompt.
        if (!o.DryRun && !settings.AssumeYes && IsInteractive()
            && !AnsiConsole.Confirm($"Proceed with {targets.Count} conversion(s)?"))
        {
            AnsiConsole.MarkupLine("[grey]aborted; nothing written.[/]");
            return 0;
        }

        var results = ProcessProjects(targets, baseDir, o);

        MigrateRenderer.RenderSummaryTable(results, baseDir, o.DryRun);
        MigrateRenderer.RenderReviewNotes(results, baseDir);
        MigrateRenderer.RenderTally(results, o.DryRun);

        var errors = results.Count(r => r.Result.Status == ConvertStatus.Error);
        var flagged = results.Count(r => r.Result.Status == ConvertStatus.Review);
        if (errors > 0) return 1;
        return flagged > 0 ? 2 : 0;
    }

    // Runs every conversion, surfacing progress with a spinner. Any per-project
    // exception is captured as an Error row rather than aborting the batch.
    private List<ProjectOutcome> ProcessProjects(List<string> targets, string baseDir, ConversionOptions o)
    {
        var results = new List<ProjectOutcome>(targets.Count);

        void RunAll(Action<string>? report)
        {
            foreach (var nf in targets)
            {
                var rel = Path.GetRelativePath(baseDir, nf);
                report?.Invoke(rel);
                ConvertResult result;
                try
                {
                    result = _converter.Convert(nf, o);
                }
                catch (Exception ex)
                {
                    result = new ConvertResult { OutputPath = nf, Error = ex.Message };
                }
                results.Add(new ProjectOutcome(nf, result));
            }
        }

        // Status() needs an interactive, non-redirected console; otherwise just
        // run straight through (Spectre would no-op the spinner anyway).
        if (IsInteractive())
        {
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start(o.DryRun ? "Analysing…" : "Converting…", ctx =>
                    RunAll(rel => ctx.Status($"{(o.DryRun ? "Analysing" : "Converting")} [blue]{Esc(rel)}[/]…")));
        }
        else
        {
            RunAll(null);
        }
        return results;
    }
}
