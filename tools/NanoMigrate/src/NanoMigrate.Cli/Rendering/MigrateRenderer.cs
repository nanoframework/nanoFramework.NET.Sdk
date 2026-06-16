using System.Text;
using NanoFramework.Migrate.Core;
using Spectre.Console;
using static NanoFramework.Migrate.Cli.Rendering.ConsoleSupport;

namespace NanoFramework.Migrate.Cli.Rendering;

/// <summary>Pairs a source .nfproj path with its conversion outcome.</summary>
internal readonly record struct ProjectOutcome(string Nfproj, ConvertResult Result);

/// <summary>Spectre presentation for the migrate command. Consumes Core's data.</summary>
internal static class MigrateRenderer
{
    public static void RenderSummaryTable(List<ProjectOutcome> results, string baseDir, bool dryRun)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.Title = new TableTitle(dryRun ? "Migration preview (dry run)" : "Migration summary");
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

            var notes = BuildNotesCell(result, dryRun);

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
    private static string BuildNotesCell(ConvertResult result, bool dryRun)
    {
        if (result.Status == ConvertStatus.Error)
            return $"[red]{Esc(result.Error ?? "error")}[/]";
        if (result.Status == ConvertStatus.Skipped)
            return "[grey]already SDK-style[/]";

        var lines = new List<string>();
        if (dryRun)
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
    public static void RenderReviewNotes(List<ProjectOutcome> results, string baseDir)
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

    public static void RenderTally(List<ProjectOutcome> results, bool dryRun)
    {
        int converted = results.Count(r => r.Result.Status == ConvertStatus.Converted);
        int skipped   = results.Count(r => r.Result.Status == ConvertStatus.Skipped);
        int flagged   = results.Count(r => r.Result.Status == ConvertStatus.Review);
        int errors    = results.Count(r => r.Result.Status == ConvertStatus.Error);
        var verb = dryRun ? "would convert" : "converted";

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
}
