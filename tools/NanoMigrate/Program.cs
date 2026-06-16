// nano-migrate — convert legacy nanoFramework .nfproj projects to the SDK-style
// MSBuild project system, one project at a time or across an entire cloned fleet.
//
// SCOPE: project-system migration ONLY. This tool does NOT touch OTA, modular
// firmware packaging, runtimes/{rid}/native layouts, or ABI manifests. It moves
// a repo from the legacy flavored .nfproj format onto an SDK-style project that
// composes over the nanoFramework SDK, folds packages.config into PackageReference,
// and folds .nuspec metadata into MSBuild Pack properties. Nothing more.
//
// The only external dependency is Spectre.Console, which drives the CLI
// presentation (rules, progress, tables, panels). It degrades gracefully when
// output is redirected or the terminal is non-interactive.

using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace NanoFramework.Migrate;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var opts = Options.Parse(args.Skip(1));
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "migrate" => CmdMigrate(opts),
                "clone"   => CmdClone(opts),
                "fleet"   => CmdFleet(opts),
                _         => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (UserError ue)
        {
            return Fail(ue.Message);
        }
    }

    // ───────────────────────────── migrate ─────────────────────────────

    private static int CmdMigrate(Options o)
    {
        Header("NanoMigrate");
        var path = o.Positional ?? throw new UserError("migrate needs a path to a .nfproj or a directory");
        var targets = ResolveProjects(path, o.Glob);

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
        if (!o.DryRun && !o.AssumeYes && IsInteractive()
            && !AnsiConsole.Confirm($"Proceed with {targets.Count} conversion(s)?"))
        {
            AnsiConsole.MarkupLine("[grey]aborted; nothing written.[/]");
            return 0;
        }

        var results = ProcessProjects(targets, baseDir, o);

        RenderSummaryTable(results, baseDir, o);
        RenderReviewNotes(results, baseDir);
        RenderTally(results, o);

        var errors = results.Count(r => r.Result.Status == ConvertStatus.Error);
        var flagged = results.Count(r => r.Result.Status == ConvertStatus.Review);
        if (errors > 0) return 1;
        return flagged > 0 ? 2 : 0;
    }

    // Runs every conversion, surfacing progress with a spinner. Any per-project
    // exception is captured as an Error row rather than aborting the batch.
    private static List<ProjectOutcome> ProcessProjects(List<string> targets, string baseDir, Options o)
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
                    result = Converter.Convert(nf, o);
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

    // ───────────────────────────── rendering ─────────────────────────────

    private static void RenderSummaryTable(List<ProjectOutcome> results, string baseDir, Options o)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.Title = new TableTitle(o.DryRun ? "Migration preview (dry run)" : "Migration summary");
        table.AddColumn("Project");
        table.AddColumn("Result");
        table.AddColumn("Packages");
        table.AddColumn("Notes");

        foreach (var (nf, result) in results)
        {
            var rel = Path.GetRelativePath(baseDir, nf);
            var (label, color) = StatusLabel(result.Status);

            var pkgs = result.Packages.Count == 0
                ? "[grey]—[/]"
                : string.Join("\n", result.Packages.Select(p => $"{Esc(p.Key)} [grey]{Esc(p.Value)}[/]"));

            var notes = BuildNotesCell(result, o);

            table.AddRow(
                new Markup($"[blue]{Esc(rel)}[/]"),
                new Markup($"[{color}]{label}[/]"),
                new Markup(pkgs),
                new Markup(notes));
        }

        AnsiConsole.Write(table);
    }

    // The Notes cell: in dry-run it previews what WOULD change (target path,
    // deletions, .sln edits); otherwise it shows the count of review flags.
    private static string BuildNotesCell(ConvertResult result, Options o)
    {
        if (result.Status == ConvertStatus.Error)
            return $"[red]{Esc(result.Error ?? "error")}[/]";
        if (result.Status == ConvertStatus.Skipped)
            return "[grey]already SDK-style[/]";

        var lines = new List<string>();
        if (o.DryRun)
        {
            lines.Add($"[grey]→[/] {Esc(Path.GetFileName(result.OutputPath))}");
            foreach (var d in result.DeletedFiles)
                lines.Add($"[red]delete[/] {Esc(Path.GetFileName(d))}");
            foreach (var s in result.UpdatedSolutions)
                lines.Add($"[yellow]edit[/] {Esc(Path.GetFileName(s))}");
        }
        if (result.Review.Count > 0)
            lines.Add($"[yellow]{result.Review.Count} item(s) need review[/]");
        return lines.Count == 0 ? "[green]clean[/]" : string.Join("\n", lines);
    }

    // Review notes get a clearly-visible yellow panel, grouped per project, so
    // they are never buried in the table.
    private static void RenderReviewNotes(List<ProjectOutcome> results, string baseDir)
    {
        var flagged = results.Where(r => r.Result.Review.Count > 0
                                      && r.Result.Status == ConvertStatus.Review).ToList();
        if (flagged.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var (nf, result) in flagged)
        {
            var rel = Path.GetRelativePath(baseDir, nf);
            sb.Append($"[bold]{Esc(rel)}[/]\n");
            foreach (var item in result.Review)
                sb.Append($"  [yellow]•[/] {Esc(item)}\n");
        }

        var panel = new Panel(sb.ToString().TrimEnd('\n'))
        {
            Header = new PanelHeader("[yellow]MANUAL REVIEW NEEDED[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow),
            Expand = true,
        };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
    }

    private static void RenderTally(List<ProjectOutcome> results, Options o)
    {
        int converted = results.Count(r => r.Result.Status == ConvertStatus.Converted);
        int skipped   = results.Count(r => r.Result.Status == ConvertStatus.Skipped);
        int flagged   = results.Count(r => r.Result.Status == ConvertStatus.Review);
        int errors    = results.Count(r => r.Result.Status == ConvertStatus.Error);
        var verb = o.DryRun ? "would convert" : "converted";

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]{verb} {converted}[/]  •  [grey]skipped {skipped}[/]  •  "
          + $"[yellow]flagged {flagged}[/]  •  [red]errors {errors}[/]  •  total {results.Count}");
    }

    private static (string label, string color) StatusLabel(ConvertStatus s) => s switch
    {
        ConvertStatus.Converted => ("Converted", "green"),
        ConvertStatus.Skipped   => ("Skipped", "grey"),
        ConvertStatus.Review    => ("Review", "yellow"),
        ConvertStatus.Error     => ("Error", "red"),
        _                       => ("?", "white"),
    };

    private static List<string> ResolveProjects(string path, string? glob)
    {
        if (File.Exists(path) && path.EndsWith(".nfproj", StringComparison.OrdinalIgnoreCase))
            return new List<string> { Path.GetFullPath(path) };
        if (!Directory.Exists(path)) return new List<string>();

        var baseDir = Path.GetFullPath(path);
        return Directory.EnumerateFiles(path, "*.nfproj", SearchOption.AllDirectories)
                        .Select(Path.GetFullPath)
                        .Where(p => glob is null || Glob.IsMatch(Path.GetRelativePath(baseDir, p), glob))
                        .OrderBy(p => p)
                        .ToList();
    }

    // ───────────────────────────── clone ─────────────────────────────

    private static int CmdClone(Options o)
    {
        Header("NanoMigrate · clone");
        var outDir = o.Positional ?? "./nano-repos";
        Directory.CreateDirectory(outDir);

        AnsiConsole.MarkupLine($"Enumerating [bold]{Esc(o.Org)}[/] repositories matching '[blue]{Esc(o.Filter)}*[/]'…");
        var repos = GitHub.ListOrgRepos(o.Org, o.Token, o.IncludeArchived)
                          .Where(r => r.Name.StartsWith(o.Filter, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(r => r.Name).ToList();

        if (repos.Count == 0) throw new UserError(
            $"no repos matched '{o.Filter}*' in org '{o.Org}'. " +
            "Check the org name and filter, or pass --token to lift the API rate limit.");

        AnsiConsole.MarkupLine($"Found [bold]{repos.Count}[/] repositories. Cloning into [blue]{Esc(outDir)}[/]…");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.Title = new TableTitle("Clone results");
        table.AddColumn("Repository");
        table.AddColumn("Result");

        int ok = 0, skipped = 0, failed = 0;
        void CloneAll(Action<string>? report)
        {
            foreach (var r in repos)
            {
                report?.Invoke(r.Name);
                var dest = Path.Combine(outDir, r.Name);
                if (Directory.Exists(dest))
                {
                    table.AddRow(new Markup($"[blue]{Esc(r.Name)}[/]"), new Markup("[grey]skipped (already present)[/]"));
                    skipped++; continue;
                }
                var (code, _, err) = Run("git", $"clone --depth 1 {r.CloneUrl} \"{dest}\"", outDir);
                if (code == 0)
                {
                    table.AddRow(new Markup($"[blue]{Esc(r.Name)}[/]"), new Markup("[green]cloned[/]"));
                    ok++;
                }
                else
                {
                    var msg = err.Trim().Split('\n').LastOrDefault() ?? "git clone failed";
                    table.AddRow(new Markup($"[blue]{Esc(r.Name)}[/]"), new Markup($"[red]FAIL[/] {Esc(msg)}"));
                    failed++;
                }
            }
        }

        if (IsInteractive())
            AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .Start("Cloning…", ctx => CloneAll(name => ctx.Status($"Cloning [blue]{Esc(name)}[/]…")));
        else
            CloneAll(null);

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]cloned {ok}[/]  •  [grey]skipped {skipped}[/]  •  [red]failed {failed}[/]");
        return failed > 0 ? 2 : 0;
    }

    // ───────────────────────────── fleet ─────────────────────────────

    private static int CmdFleet(Options o)
    {
        Header("NanoMigrate · fleet");
        var reposDir = o.Positional ?? throw new UserError("fleet needs a path to a directory of cloned repos");
        if (!Directory.Exists(reposDir)) throw new UserError($"directory not found: {reposDir}");
        if (o.Commit && o.Branch is null) throw new UserError("--commit requires --branch");
        // nanoFramework workflow: branch names must not start with "develop" (they
        // collide with upstream develop-* branches).
        if (o.Branch is not null && o.Branch.StartsWith("develop", StringComparison.OrdinalIgnoreCase))
            throw new UserError("branch name must not start with 'develop' (nanoFramework workflow); "
                              + "use something like 'sdk-migration' or 'issue-123'");
        // In a git repo the commit history already preserves the pre-migration file,
        // so a .bak alongside it is just noise in the diff. Skip backups when committing.
        if (o.Commit) o.NoBackup = true;

        // A repo qualifies if it contains at least one .nfproj that survives the
        // glob filter (default: any .nfproj).
        bool RepoMatches(string d) =>
            NfprojUnder(d, o.Glob).Any();

        var repoDirs = Directory.EnumerateDirectories(reposDir)
                                .Where(RepoMatches)
                                .OrderBy(d => d).ToList();
        if (repoDirs.Count == 0)
            throw new UserError(o.Glob is null
                ? $"no repos containing .nfproj found under '{reposDir}'"
                : $"no repos with .nfproj matching glob '{o.Glob}' found under '{reposDir}'");

        if (o.DryRun)
            AnsiConsole.MarkupLine($"[yellow]Dry run[/] — {repoDirs.Count} repo(s); nothing will be written.");
        else
            AnsiConsole.MarkupLine($"Processing [bold]{repoDirs.Count}[/] repo(s) under [blue]{Esc(reposDir)}[/]"
                + (o.Branch is not null ? $" on branch [blue]{Esc(o.Branch)}[/]" : "")
                + (o.Commit ? ", committing" : "") + ".");
        AnsiConsole.WriteLine();

        var report = new List<RepoReport>();

        void FleetAll(Action<string>? progress)
        {
            foreach (var repo in repoDirs)
            {
                progress?.Invoke(Path.GetFileName(repo));
                var rr = new RepoReport { Name = Path.GetFileName(repo) };
                try
                {
                    if (o.Branch is not null && !o.DryRun)
                    {
                        var (code, _, err) = Run("git", $"checkout -B {o.Branch}", repo);
                        if (code != 0) { rr.Error = $"git checkout failed: {err.Trim()}"; report.Add(rr); continue; }
                    }

                    foreach (var nf in NfprojUnder(repo, o.Glob).OrderBy(p => p))
                    {
                        rr.Projects++;
                        var result = Converter.Convert(nf, o);
                        var rel = Path.GetRelativePath(repo, nf);
                        foreach (var item in result.Review) rr.Review.Add($"{rel}: {item}");
                    }

                    if (o.Commit && !o.DryRun)
                    {
                        Run("git", "add -A", repo);
                        var msgFile = WriteCommitMessage(repo, o);
                        var signOff = o.SignOff ? "-s " : "";
                        var (code, _, err) = Run("git", $"commit {signOff}-F \"{msgFile}\"", repo);
                        File.Delete(msgFile);
                        rr.Committed = code == 0;
                        if (code != 0 && !err.Contains("nothing to commit"))
                        {
                            rr.Error = err.Contains("Please tell me who you are") || err.Contains("user.name")
                                ? "git commit failed: set git user.name/user.email (real name) so the "
                                  + "Signed-off-by line is valid, or pass --no-sign-off"
                                : $"git commit: {err.Trim()}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    rr.Error = ex.Message;
                }
                report.Add(rr);
            }
        }

        if (IsInteractive())
            AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .Start("Migrating…", ctx => FleetAll(name => ctx.Status($"Migrating [blue]{Esc(name)}[/]…")));
        else
            FleetAll(null);

        RenderFleetTable(report);
        RenderFleetReview(report);

        WriteReport(report, o, reposDir);
        var errored = report.Count(r => r.Error is not null);
        var needsReview = report.Count(r => r.Error is null && r.Review.Count > 0);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold]{report.Count}[/] repo(s) processed  •  "
          + $"[yellow]{needsReview} need review[/]  •  [red]{errored} with errors[/]  •  "
          + $"report: [blue]{Esc(o.Report)}[/]");
        return errored > 0 ? 2 : 0;
    }

    private static void RenderFleetTable(List<RepoReport> report)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.Title = new TableTitle("Fleet results");
        table.AddColumn("Repository");
        table.AddColumn("Result");
        table.AddColumn("Projects");
        table.AddColumn("Review");

        foreach (var rr in report)
        {
            string label, color;
            if (rr.Error is not null) { label = "Error"; color = "red"; }
            else if (rr.Review.Count > 0) { label = "Review"; color = "yellow"; }
            else { label = rr.Committed ? "OK (committed)" : "OK"; color = "green"; }

            var note = rr.Error is not null ? $" [red]{Esc(rr.Error.Split('\n')[0])}[/]" : "";
            table.AddRow(
                new Markup($"[blue]{Esc(rr.Name)}[/]"),
                new Markup($"[{color}]{label}[/]{note}"),
                new Markup(rr.Projects.ToString()),
                new Markup(rr.Review.Count == 0 ? "[grey]—[/]" : $"[yellow]{rr.Review.Count}[/]"));
        }
        AnsiConsole.Write(table);
    }

    private static void RenderFleetReview(List<RepoReport> report)
    {
        var flagged = report.Where(r => r.Error is null && r.Review.Count > 0).ToList();
        if (flagged.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var rr in flagged)
        {
            sb.Append($"[bold]{Esc(rr.Name)}[/]\n");
            foreach (var item in rr.Review) sb.Append($"  [yellow]•[/] {Esc(item)}\n");
        }
        var panel = new Panel(sb.ToString().TrimEnd('\n'))
        {
            Header = new PanelHeader("[yellow]MANUAL REVIEW NEEDED[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow),
            Expand = true,
        };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
    }

    // Enumerates the .nfproj under a repo that survive the glob filter (matched
    // against the path relative to the repo directory).
    private static IEnumerable<string> NfprojUnder(string repoDir, string? glob)
    {
        var baseDir = Path.GetFullPath(repoDir);
        foreach (var nf in Directory.EnumerateFiles(repoDir, "*.nfproj", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(nf);
            if (glob is null || Glob.IsMatch(Path.GetRelativePath(baseDir, full), glob))
                yield return full;
        }
    }

    private static void WriteReport(List<RepoReport> report, Options o, string reposDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# nanoFramework SDK-style migration — fleet report\n");
        sb.AppendLine($"- Source: `{Path.GetFullPath(reposDir)}`");
        sb.AppendLine($"- Mode: {(o.DryRun ? "dry-run (no files written)" : "applied")}"
                    + (o.Branch is not null ? $", branch `{o.Branch}`" : "")
                    + (o.Commit ? ", committed" : ""));
        sb.AppendLine($"- SDK `nanoFramework.NET.Sdk` (versionless), TFM `{o.Tfm}`, output extension `{o.Ext}`\n");

        int total = report.Count, clean = report.Count(r => r.Error is null && r.Review.Count == 0);
        int needsReview = report.Count(r => r.Error is null && r.Review.Count > 0);
        int errors = report.Count(r => r.Error is not null);
        sb.AppendLine("## Summary\n");
        sb.AppendLine($"| Repos | Clean | Needs review | Errored |");
        sb.AppendLine($"|------:|------:|-------------:|--------:|");
        sb.AppendLine($"| {total} | {clean} | {needsReview} | {errors} |\n");

        if (errors > 0)
        {
            sb.AppendLine("## Errored repos\n");
            foreach (var r in report.Where(r => r.Error is not null))
                sb.AppendLine($"- **{r.Name}** — {r.Error}");
            sb.AppendLine();
        }

        if (needsReview > 0)
        {
            sb.AppendLine("## Repos needing manual review\n");
            sb.AppendLine("These migrated, but the tool could not confidently resolve everything. "
                        + "Each line is something a human should confirm before merging.\n");
            foreach (var r in report.Where(r => r.Error is null && r.Review.Count > 0))
            {
                sb.AppendLine($"### {r.Name}\n");
                foreach (var item in r.Review) sb.AppendLine($"- {item}");
                sb.AppendLine();
            }
        }

        if (clean > 0)
        {
            sb.AppendLine("## Clean migrations\n");
            sb.AppendLine("Converted with no items flagged for review:\n");
            foreach (var r in report.Where(r => r.Error is null && r.Review.Count == 0))
                sb.AppendLine($"- {r.Name} ({r.Projects} project(s))"
                            + (r.Committed ? " — committed" : ""));
            sb.AppendLine();
        }

        File.WriteAllText(o.Report, sb.ToString());
    }

    // ───────────────────────────── helpers ─────────────────────────────

    // Builds a commit message that follows the nanoFramework guidance: a short
    // summary (<= 50 chars), a blank line, a body wrapped at 72 columns, and an
    // optional "Fix #<issue>" trailer. Returns the path to a temp message file.
    private static string WriteCommitMessage(string repo, Options o)
    {
        var summary = o.CommitMessage ?? "Migrate project system to SDK-style";
        if (summary.Length > 50) summary = summary[..50].TrimEnd();

        var body = Wrap(
            "Convert the legacy .nfproj project system to an SDK-style MSBuild project: "
          + "drop project-system boilerplate, fold packages.config into PackageReference, "
          + "and fold .nuspec metadata into MSBuild Pack properties. "
          + "No functional code changes.", 72);

        var sb = new StringBuilder();
        sb.Append(summary).Append("\n\n").Append(body).Append('\n');
        if (o.Issue is not null) sb.Append("\nFix #").Append(o.Issue).Append('\n');

        var path = Path.GetTempFileName();
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static string Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        int lineLen = 0;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (lineLen > 0 && lineLen + 1 + word.Length > width) { sb.Append('\n'); lineLen = 0; }
            else if (lineLen > 0) { sb.Append(' '); lineLen++; }
            sb.Append(word); lineLen += word.Length;
        }
        return sb.ToString();
    }

    internal static (int code, string stdout, string stderr) Run(string file, string args, string cwd)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, so, se);
    }

    // A "title rule" header. Spectre renders this as a centred rule when the
    // terminal is wide enough, and degrades to plain text when redirected.
    private static void Header(string title)
    {
        var rule = new Rule($"[bold]{Esc(title)}[/]") { Justification = Justify.Left };
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
    }

    // Escapes interpolated text (notably file paths) so Spectre markup like
    // "[" in a path can't be interpreted as styling — guards against injection.
    private static string Esc(string s) => Markup.Escape(s);

    // Interactive == an attached terminal whose stdin is not redirected. We use
    // this to decide whether to show a spinner and whether to prompt. In
    // non-interactive contexts (CI, piped input) we never prompt or block.
    private static bool IsInteractive() =>
        !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive;

    private static bool IsHelp(string a) => a is "-h" or "--help" or "help";

    private static int Fail(string msg)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {Esc(msg)}");
        return 1;
    }

    private static void PrintUsage() => AnsiConsole.WriteLine("""
        nano-migrate — migrate nanoFramework projects to the SDK-style project system

        USAGE
          nano-migrate migrate <path>      Convert a .nfproj, or every .nfproj under a directory.
          nano-migrate clone   <out-dir>   Clone all matching repos from a GitHub org.
          nano-migrate fleet   <repos-dir> Migrate every .nfproj across cloned repos; write a report.

        RECOMMENDED WORKFLOW
          Dry-run a directory first to preview every change, review the table, then
          run for real:
            nano-migrate migrate ./samples --dry-run
            nano-migrate migrate ./samples            # prompts once before writing

        COMMON OPTIONS
          --sdk <version>     Accepted for back-compat but ignored: the SDK reference
                              is versionless (pinned via global.json msbuild-sdks).
          --tfm <moniker>     Target framework moniker                 (default netnano1.0)
          --ext <ext>         Output extension: .nfproj or .csproj     (default .csproj)
          --no-backup         Don't write a .nfproj.bak (implied by fleet --commit).
          --dry-run           Analyse and preview only; write nothing.
          --glob <pattern>    Only convert .nfproj whose path (relative to <path>)
                              matches the glob. Supports *, ** and ?. Default: all
                              .nfproj recursively. Example: --glob "Beginner/**".
          --yes, -y           Skip the interactive "Proceed?" confirmation on a real run.
                              (Non-interactive/redirected runs never prompt regardless.)

        clone OPTIONS
          --org <name>        GitHub org                               (default nanoframework)
          --filter <prefix>   Repo name prefix to match                (default lib-)
          --token <pat>       GitHub token (or env GITHUB_TOKEN) to raise the API rate limit.
          --include-archived  Include archived repositories (skipped by default).

        fleet OPTIONS
          --report <path>     Markdown report path             (default migration-report.md)
          --branch <name>     Create/reset this git branch in each repo (must not start with 'develop').
          --commit            Commit the changes (requires --branch). Uses a contribution-compliant
                              message and signs off (Signed-off-by) by default.
          --message <msg>     Commit summary line (kept <= 50 chars).
          --issue <n>         Reference an issue: adds a "Fix #<n>" trailer to the commit.
          --no-sign-off       Don't add a Signed-off-by line.
          --glob <pattern>    Only convert matching .nfproj within each repo (see above).

        SCOPE
          Project-system migration only. Does NOT produce OTA artifacts, modular
          firmware packaging, runtimes/{rid}/native layouts, or ABI manifests.

        EXAMPLES
          nano-migrate migrate ./lib-CoreLibrary
          nano-migrate migrate ./samples --glob "Beginner/**" --dry-run
          nano-migrate migrate ./MyDevice/MyDevice.nfproj --ext .csproj --yes
          nano-migrate clone ./nano-repos --token $GITHUB_TOKEN
          nano-migrate fleet ./nano-repos --branch sdk-migration --commit --dry-run
        """);
}

/// <summary>Pairs a source .nfproj path with its conversion outcome.</summary>
internal readonly record struct ProjectOutcome(string Nfproj, ConvertResult Result);
