using System.ComponentModel;
using NanoFramework.Migrate.Core;
using NanoFramework.Migrate.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;
using static NanoFramework.Migrate.Cli.Rendering.ConsoleSupport;

namespace NanoFramework.Migrate.Cli.Commands;

public sealed class MigrateSettings : CommandSettings
{
    [CommandArgument(0, "<path>")]
    [Description("A .nfproj file, a solution (.sln/.slnx), or a directory. "
               + "For a solution, only its referenced .nfproj are converted and the solution is retargeted. "
               + "For a directory, discovered solutions drive the selection; with no solution found, every .nfproj under the directory is converted.")]
    public string Path { get; init; } = "";

    [CommandOption("--solution <path>")]
    [Description("Migrate only this solution (.sln or .slnx): convert just its referenced .nfproj and retarget that one solution. Overrides directory discovery.")]
    public string? Solution { get; init; }

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
    [Description("Only convert .nfproj whose path (relative to <path>) matches the glob; the solutions referencing any matched project are updated. Supports *, ** and ?. Example: \"Beginner/**\".")]
    public string? Glob { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Skip interactive prompts (Proceed? / solution selection): select all affected solutions and proceed. (Non-interactive runs never prompt regardless.)")]
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

public sealed class MigrateCommand : Command<MigrateSettings>
{
    private readonly IProjectConverter _converter = new ProjectConverter();

    protected override int Execute(CommandContext context, MigrateSettings settings, CancellationToken cancellationToken)
    {
        Header("NanoMigrate");

        var o = settings.ToConversionOptions();
        var plan = MigrationPlanner.Plan(settings.Path, settings.Solution, settings.Glob);

        // Solution-aware plans (explicit solution, directory-with-solutions, glob)
        // are handled by their own path; loose/single plans keep the historical flow.
        return plan.Kind switch
        {
            PlanKind.SingleProject or PlanKind.LooseDirectory => RunLoose(settings, o, plan),
            _ => RunSolutionScoped(settings, o, plan),
        };
    }

    // The historical flow: a single .nfproj or a directory with no solutions. The
    // converter retargets any solutions it finds by walking up the tree.
    private int RunLoose(MigrateSettings settings, ConversionOptions o, MigrationPlan plan)
    {
        var targets = plan.LooseProjects.ToList();

        // Reentrant: a fully-converted tree has no .nfproj left. Exit cleanly (0).
        if (targets.Count == 0)
        {
            var why = o.Glob is null
                ? $"no .nfproj found under '{Esc(settings.Path)}' (already SDK-style?)."
                : $"no .nfproj matched glob '{Esc(o.Glob)}' under '{Esc(settings.Path)}'.";
            AnsiConsole.MarkupLine($"[grey]nothing to convert: {why}[/]");
            return 0;
        }

        var baseDir = BaseDirFor(settings.Path);

        AnsiConsole.MarkupLine(o.DryRun
            ? $"[yellow]Dry run[/] — analysing [bold]{targets.Count}[/] project(s) under [blue]{Esc(baseDir)}[/]. Nothing will be written."
            : $"Found [bold]{targets.Count}[/] project(s) to convert under [blue]{Esc(baseDir)}[/].");
        AnsiConsole.WriteLine();

        if (!Confirm($"Proceed with {targets.Count} conversion(s)?", settings, o)) return AbortedExit();

        var results = ProcessProjects(targets, baseDir, o);
        return Report(results, baseDir, o, rewritten: Array.Empty<string>());
    }

    // The solution-aware flow: pick the candidate solutions (multi-select / confirm),
    // convert their .nfproj, then retarget exactly those solutions ourselves.
    private int RunSolutionScoped(MigrateSettings settings, ConversionOptions o, MigrationPlan plan)
    {
        var baseDir = BaseDirFor(settings.Path);

        if (plan.Candidates.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]nothing to convert: the selected solution(s) reference no .nfproj (already SDK-style?).[/]");
            return 0;
        }

        // Show what is in scope before any prompt.
        MigrateRenderer.RenderCandidateSolutions(plan.Candidates, baseDir);

        var chosen = SelectSolutions(plan, settings, o);
        if (chosen.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]aborted; no solution selected, nothing written.[/]");
            return 0;
        }

        var targets = MigrationPlan.ProjectsOf(chosen);
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]nothing to convert: the chosen solution(s) reference no .nfproj.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine(o.DryRun
            ? $"[yellow]Dry run[/] — analysing [bold]{targets.Count}[/] project(s) across [bold]{chosen.Count}[/] solution(s). Nothing will be written."
            : $"Converting [bold]{targets.Count}[/] project(s) across [bold]{chosen.Count}[/] solution(s).");
        AnsiConsole.WriteLine();

        if (!Confirm($"Proceed with {targets.Count} conversion(s) and update {chosen.Count} solution(s)?", settings, o))
            return AbortedExit();

        // The host owns solution retargeting here, so the converter must not also
        // walk up and rewrite solutions the user did not select.
        var convOpts = o with { SkipSolutionRewrite = true };
        var results = ProcessProjects(targets, baseDir, convOpts);

        // Retarget exactly the chosen solutions to the converted projects. Only
        // projects that actually converted (a .csproj now exists / would exist) are
        // handed to the rewriter; idempotent on re-run.
        var converted = results
            .Where(r => r.Result.Status is ConvertStatus.Converted or ConvertStatus.Review)
            .Select(r => r.Nfproj)
            .ToList();

        var rewritten = new List<string>();
        if (!o.DryRun && converted.Count > 0)
        {
            foreach (var c in chosen)
                if (SolutionRewriter.RewriteFile(c.Solution, converted))
                    rewritten.Add(c.Solution.Path);
        }

        return Report(results, baseDir, o, rewritten);
    }

    // Decides which candidate solutions to operate on. Explicit single solution: no
    // choice. Otherwise multi-select when interactive; non-interactive / --yes
    // selects all affected (announcing it).
    private static List<SolutionCandidate> SelectSolutions(MigrationPlan plan, MigrateSettings settings, ConversionOptions o)
    {
        // An explicit single solution (positional .sln/.slnx or --solution) is the
        // only candidate; there is nothing to choose.
        if (!plan.RequiresSelection)
            return plan.Candidates.ToList();

        var all = plan.Candidates.ToList();

        // Only one affected solution: a simple confirm, not a multi-select.
        if (all.Count == 1)
        {
            if (settings.AssumeYes || o.DryRun || !IsInteractive())
            {
                if (!settings.AssumeYes && !o.DryRun)
                    AnsiConsole.MarkupLine("[grey]non-interactive: selecting the only affected solution.[/]");
                return all;
            }
            var fmt = all[0].Solution.Format == SolutionFormat.Xml ? "slnx" : "sln";
            return AnsiConsole.Confirm($"Migrate solution '{Esc(Path.GetFileName(all[0].Solution.Path))}' ({fmt})?")
                ? all : new List<SolutionCandidate>();
        }

        // Several solutions: CI / non-interactive selects all and proceeds.
        if (settings.AssumeYes || o.DryRun || !IsInteractive())
        {
            if (!settings.AssumeYes && !o.DryRun)
                AnsiConsole.MarkupLine($"[grey]non-interactive: selecting all {all.Count} affected solution(s).[/]");
            return all;
        }

        // Interactive: present the multi-select. Picking none aborts cleanly.
        var prompt = new MultiSelectionPrompt<SolutionCandidate>()
            .Title("Select the solution(s) to migrate")
            .NotRequired()                       // picking none is allowed (aborts)
            .PageSize(15)
            .InstructionsText("[grey](space to toggle, enter to confirm; pick none to abort)[/]")
            .UseConverter(c =>
            {
                var fmt = c.Solution.Format == SolutionFormat.Xml ? "slnx" : "sln";
                return $"{Path.GetFileName(c.Solution.Path)} ({fmt}, {c.NanoProjects.Count} project(s))";
            });
        prompt.AddChoices(all);
        return AnsiConsole.Prompt(prompt);
    }

    // The shared confirm gate. Returns true when we should proceed. In dry-run, when
    // --yes is set, or in a non-interactive context we never block.
    private static bool Confirm(string question, MigrateSettings settings, ConversionOptions o)
    {
        if (o.DryRun || settings.AssumeYes || !IsInteractive()) return true;
        return AnsiConsole.Confirm(question);
    }

    private static int AbortedExit()
    {
        AnsiConsole.MarkupLine("[grey]aborted; nothing written.[/]");
        return 0;
    }

    // The base directory glob/relative paths are reported against.
    private static string BaseDirFor(string path) =>
        Directory.Exists(path) ? Path.GetFullPath(path)
        : File.Exists(path)    ? Path.GetDirectoryName(Path.GetFullPath(path))!
                               : Path.GetFullPath(path);

    // Renders the summary/review/tally and (for solution-scoped runs) the rewritten
    // solutions, then maps the outcome to the exit code (0 clean / 2 review / 1 error).
    private static int Report(List<ProjectOutcome> results, string baseDir, ConversionOptions o, IReadOnlyList<string> rewritten)
    {
        MigrateRenderer.RenderSummaryTable(results, baseDir, o.DryRun);
        MigrateRenderer.RenderReviewNotes(results, baseDir);
        MigrateRenderer.RenderRewrittenSolutions(rewritten, baseDir);
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
